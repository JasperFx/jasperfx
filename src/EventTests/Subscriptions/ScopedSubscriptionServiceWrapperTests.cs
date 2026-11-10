using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Subscriptions;

// jasperfx#1050: Marten and Fisher each carry a copy of this wrapper because it was internal. Nothing
// ever called it, which is how it could be unable to run: see the first two facts.
public class ScopedSubscriptionServiceWrapperTests
{
    /// <summary>What a store's own ISubscription is: the type its ISubscriptionRunner is closed over.</summary>
    public interface IStoreSubscription
    {
        void Process(int range);
    }

    public class Tracked : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Only IAsyncDisposable, which a synchronous scope disposal throws on.</summary>
    public class AsyncOnly : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public class BareSubscription(Tracked tracked) : IStoreSubscription
    {
        public static readonly List<Tracked> Seen = [];

        public Tracked Tracked { get; } = tracked;

        public void Process(int range) => Seen.Add(Tracked);
    }

    public class ConfiguredSubscription
        : JasperFxSubscriptionBase<FakeOperations, FakeSession, IStoreSubscription>, IStoreSubscription
    {
        public ConfiguredSubscription()
        {
            Name = "Configured";
            Version = 3;
            Options.BatchSize = 42;
            IncludeType<ScopedSubscriptionServiceWrapperTests>();
            StreamType = typeof(BareSubscription);
            IncludeArchivedEvents = true;
        }

        public void Process(int range)
        {
        }
    }

    public class NeedsAsyncDisposal(AsyncOnly dependency, Tracked tracked) : IStoreSubscription
    {
        public AsyncOnly Dependency { get; } = dependency;
        public Tracked Tracked { get; } = tracked;

        public void Process(int range)
        {
        }
    }

    private static ScopedSubscriptionServiceWrapper<T, FakeOperations, FakeSession, IStoreSubscription>
        wrapperFor<T>(Action<IServiceCollection>? configure = null) where T : class, IStoreSubscription
    {
        var services = new ServiceCollection();
        services.AddScoped<Tracked>();
        services.AddScoped<T>();
        configure?.Invoke(services);

        return new ScopedSubscriptionServiceWrapper<T, FakeOperations, FakeSession, IStoreSubscription>(
            services.BuildServiceProvider());
    }

    private static IEventStore<FakeOperations, FakeSession> aStore(
        Action<IStoreSubscription>? execute = null)
    {
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>, ISubscriptionRunner<IStoreSubscription>>();

        ((ISubscriptionRunner<IStoreSubscription>)store)
            .ExecuteAsync(Arg.Any<IStoreSubscription>(), Arg.Any<IEventDatabase>(), Arg.Any<EventRange>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                execute?.Invoke(call.ArgAt<IStoreSubscription>(0));
                return Task.CompletedTask;
            });

        return store;
    }

    private static SubscriptionExecutionBase executionFor<T>(
        ScopedSubscriptionServiceWrapper<T, FakeOperations, FakeSession, IStoreSubscription> wrapper,
        IEventStore<FakeOperations, FakeSession> store) where T : notnull, IStoreSubscription
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");

        return (SubscriptionExecutionBase)wrapper.BuildExecution(store, database, NullLogger.Instance,
            wrapper.ShardNames().Single());
    }

    private static Task processARangeAsync(SubscriptionExecutionBase execution)
        => execution.ProcessRangeAsync(new EventRange(Substitute.For<ISubscriptionAgent>(), 0, 10));

    /// <remarks>
    /// The runner was looked up as ISubscriptionRunner&lt;T&gt; over the concrete subscription. Every store
    /// implements it over its own ISubscription, and the interface is not variant, so making the wrapper
    /// public without fixing this ships a type that throws ArgumentOutOfRangeException on every start.
    /// </remarks>
    [Fact]
    public void an_execution_can_be_built_against_a_store_shaped_the_way_stores_are()
    {
        var wrapper = wrapperFor<BareSubscription>();

        var execution = wrapper.BuildExecution(aStore(), Substitute.For<IEventDatabase>(), NullLogger.Instance,
            wrapper.ShardNames().Single());

        execution.ShouldNotBeNull();
    }

    [Fact]
    public void an_execution_can_be_built_through_the_logger_factory_overload()
    {
        var wrapper = wrapperFor<BareSubscription>();

        var execution = wrapper.BuildExecution(aStore(), Substitute.For<IEventDatabase>(),
            NullLoggerFactory.Instance, wrapper.ShardNames().Single());

        execution.ShouldNotBeNull();
    }

    [Fact]
    public void a_store_that_is_no_runner_at_all_is_still_told_so()
    {
        var wrapper = wrapperFor<BareSubscription>();

        Should.Throw<ArgumentOutOfRangeException>(() => wrapper.BuildExecution(
            Substitute.For<IEventStore<FakeOperations, FakeSession>>(), Substitute.For<IEventDatabase>(),
            NullLogger.Instance, wrapper.ShardNames().Single()));
    }

    [Fact]
    public async Task the_store_runs_the_subscription_the_container_built()
    {
        IStoreSubscription? ran = null;

        await processARangeAsync(executionFor(wrapperFor<BareSubscription>(), aStore(s => ran = s)));

        ran.ShouldBeOfType<BareSubscription>();
    }

    [Fact]
    public async Task every_range_gets_its_own_scope()
    {
        var seen = new List<BareSubscription>();
        var execution = executionFor(wrapperFor<BareSubscription>(), aStore(s => seen.Add((BareSubscription)s)));

        await processARangeAsync(execution);
        await processARangeAsync(execution);

        seen.Count.ShouldBe(2);
        seen[0].Tracked.ShouldNotBeSameAs(seen[1].Tracked);
    }

    /// <remarks>
    /// A subscription's listener runs after ProcessEventsAsync returns, inside the store's runner. The
    /// Marten and Fisher copies closed the scope at that return, so a listener touching anything scoped
    /// found it disposed. The scope has to go round the runner.
    /// </remarks>
    [Fact]
    public async Task the_scope_is_open_for_all_of_the_stores_run_and_closed_afterward()
    {
        Tracked? tracked = null;
        var disposedDuringTheRun = true;

        var execution = executionFor(wrapperFor<BareSubscription>(), aStore(s =>
        {
            tracked = ((BareSubscription)s).Tracked;
            disposedDuringTheRun = tracked.IsDisposed;
        }));

        await processARangeAsync(execution);

        disposedDuringTheRun.ShouldBeFalse();
        tracked.ShouldNotBeNull().IsDisposed.ShouldBeTrue();
    }

    /// <remarks>
    /// marten#4318, which both store copies fixed: the wrapper is what the daemon reads, so what the
    /// subscription's constructor set has to end up on it.
    /// </remarks>
    [Fact]
    public void what_the_subscription_configured_for_itself_reaches_the_daemon()
    {
        var wrapper = wrapperFor<ConfiguredSubscription>();

        wrapper.Name.ShouldBe("Configured");
        wrapper.Version.ShouldBe(3u);
        wrapper.Options.BatchSize.ShouldBe(42);
        wrapper.IncludedEventTypes.ShouldBe([typeof(ScopedSubscriptionServiceWrapperTests)]);
        wrapper.StreamType.ShouldBe(typeof(BareSubscription));
        wrapper.IncludeArchivedEvents.ShouldBeTrue();

        var shard = wrapper.Shards().ShouldHaveSingleItem();
        shard.Options.ShouldBeSameAs(wrapper.Options);
        shard.Name.Identity.ShouldBe(wrapper.ShardNames().Single().Identity);
        shard.Name.Identity.ShouldContain("Configured");
    }

    [Fact]
    public void a_bare_subscription_is_named_for_its_type()
    {
        var wrapper = wrapperFor<BareSubscription>();

        wrapper.Name.ShouldBe(nameof(BareSubscription));
        wrapper.Version.ShouldBe(1u);
        wrapper.Type.ShouldBe(SubscriptionType.Subscription);
        wrapper.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        wrapper.ImplementationType.ShouldBe(typeof(BareSubscription));
    }

    /// <remarks>
    /// Both stores configure a registration through ISubscriptionOptions, and that has to land on what
    /// the daemon reads.
    /// </remarks>
    [Fact]
    public void a_registration_can_configure_it_through_the_options_interface()
    {
        var wrapper = wrapperFor<BareSubscription>();

        ISubscriptionOptions options = wrapper;
        options.Name = "Renamed";
        options.Version = 4;
        options.Options.BatchSize = 7;
        options.IncludeType<ScopedSubscriptionServiceWrapperTests>();

        wrapper.ShardNames().Single().Identity.ShouldContain("Renamed");
        wrapper.Shards().Single().Options.BatchSize.ShouldBe(7);
        wrapper.Version.ShouldBe(4u);
        wrapper.IncludedEventTypes.ShouldContain(typeof(ScopedSubscriptionServiceWrapperTests));
    }

    /// <remarks>
    /// Synchronously disposing a scope throws on the first service that is only IAsyncDisposable and
    /// abandons the rest, and SafeDispose swallowed that. This is the probe scope in the constructor.
    /// </remarks>
    [Fact]
    public void the_constructors_probe_scope_disposes_what_it_built()
    {
        AsyncOnly? dependency = null;
        Tracked? tracked = null;

        wrapperFor<NeedsAsyncDisposal>(services =>
        {
            services.AddScoped(_ => dependency = new AsyncOnly());
            services.AddScoped(_ => tracked = new Tracked());
        });

        dependency.ShouldNotBeNull().IsDisposed.ShouldBeTrue();
        tracked.ShouldNotBeNull().IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void a_subscription_the_container_cannot_build_says_so_rather_than_leaking_the_scope()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() =>
            new ScopedSubscriptionServiceWrapper<BareSubscription, FakeOperations, FakeSession, IStoreSubscription>(
                services.BuildServiceProvider()));
    }

    /// <remarks>
    /// The issue is the reach. A store outside the InternalsVisibleTo list has to be able to name the
    /// wrapper, and only the wrapper: a store sees the execution as an ISubscriptionExecution.
    /// </remarks>
    [Fact]
    public void only_the_wrapper_is_public()
    {
        typeof(ScopedSubscriptionServiceWrapper<,,,>).IsPublic.ShouldBeTrue();
        typeof(ScopedSubscriptionServiceWrapper<,,,>).Assembly
            .GetType("JasperFx.Events.Subscriptions.ScopedSubscriptionExecution`2")
            .ShouldNotBeNull().IsPublic.ShouldBeFalse();
    }
}
