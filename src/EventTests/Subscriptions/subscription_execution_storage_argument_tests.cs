using System.Diagnostics.Metrics;
using EventTests.Daemon;
using EventTests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Subscriptions;

// jasperfx#827: SubscriptionExecution<T> resolves its ISubscriptionRunner<T> from the `storage`
// constructor argument, and it is the STORE that implements ISubscriptionRunner<T> -- an
// IEventDatabase never does. JasperFxSubscriptionBase has two BuildExecution overloads and they
// disagreed about which one to hand over: the ILogger overload passed the store, while the
// explicit-interface ILoggerFactory overload passed the database. JasperFxAsyncDaemon.buildAgentForShard
// picks the ILoggerFactory overload whenever the daemon was built with an ILoggerFactory -- which is
// every hosted/coordinator-run daemon -- so every subscription started that way blew up on
// construction with ArgumentOutOfRangeException. The fixture-built daemons the subscription compliance
// suites drive take the ILogger constructor, which is why no shared suite covered it.
public class subscription_execution_storage_argument_tests
{
    [Fact]
    public void logger_factory_overload_resolves_the_runner_off_the_store()
    {
        var (store, database, subscription) = build();

        var execution = ((ISubscriptionFactory<FakeOperations, FakeSession>)subscription)
            .BuildExecution(store, database, NullLoggerFactory.Instance, subscription.ShardNames().Single());

        execution.ShouldNotBeNull();
    }

    [Fact]
    public void logger_overload_resolves_the_runner_off_the_store()
    {
        var (store, database, subscription) = build();

        var execution =
            subscription.BuildExecution(store, database, NullLogger.Instance, subscription.ShardNames().Single());

        execution.ShouldNotBeNull();
    }

    [Fact]
    public async Task subscription_starts_through_a_daemon_built_with_a_logger_factory()
    {
        // The hosted / projection-coordinator path: JasperFxAsyncDaemon's ILoggerFactory constructor,
        // which makes buildAgentForShard take the explicit-interface BuildExecution overload.
        var (store, database, subscription) = build();
        var shardName = subscription.ShardNames().Single();
        store.AllShards().Returns(subscription.Shards());

        var detector = Substitute.For<IHighWaterDetector>();
        detector.DatabaseUri.Returns(new Uri("fake://db1"));
        detector.Detect(Arg.Any<CancellationToken>()).Returns(new HighWaterStatistics());
        detector.DetectInSafeZone(Arg.Any<CancellationToken>()).Returns(new HighWaterStatistics());

        var daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
            store, database, NullLoggerFactory.Instance, detector, new FakeProjectionGraph());

        try
        {
            await daemon.StartAgentAsync(shardName.Identity, TestContext.Current.CancellationToken);

            daemon.CurrentAgents().ShouldHaveSingleItem()
                .Name.Identity.ShouldBe(shardName.Identity);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    private static (IEventStore<FakeOperations, FakeSession> store, IEventDatabase database,
        FakeSubscriptionSource subscription) build()
    {
        // The store is the ISubscriptionRunner; the database deliberately is not.
        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>, ISubscriptionRunner<FakeSubscription>>();
        store.Meter.Returns(new Meter("tests"));
        store.TimeProvider.Returns(TimeProvider.System);
        store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
        store.ContinuousErrors.Returns(new ErrorHandlingOptions());
        store.RebuildErrors.Returns(new ErrorHandlingOptions());

        var loader = Substitute.For<IEventLoader>();
        loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<EventRequest>();
                var page = new EventPage(request.Floor);
                page.CalculateCeiling(request.BatchSize, request.HighWater);
                return Task.FromResult(page);
            });
        store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
            Arg.Any<AsyncOptions>()).Returns(loader);
        store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
            Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.DatabaseUri.Returns(new Uri("fake://db1"));
        database.Tracker.Returns(new ShardStateTracker(new NulloLogger()));

        return (store, database, new FakeSubscriptionSource(new FakeSubscription()));
    }

    public class FakeSubscription;

    public class FakeSubscriptionSource(FakeSubscription subscription)
        : JasperFxSubscriptionBase<FakeOperations, FakeSession, FakeSubscription>(subscription);

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }
}
