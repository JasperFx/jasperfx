using System.Diagnostics.Metrics;
using EventTests.Projections;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#1134 / marten#5696: CatchUpAsync used to learn about a shard's failure only through a Recorder
// listening on Database.Tracker. The tracker records a state synchronously but DELIVERS it to listeners
// from its block's consumer thread, so CatchUpAsync could return cleanly before the Recorder ever saw the
// failure. A slow listener ahead of the Recorder makes that race deterministic.
public class CatchUpSurfacesShardFailureTests
{
    [Fact]
    public async Task catch_up_throws_when_a_shard_faults_even_if_the_tracker_delivers_slowly()
    {
        await using var harness = new Harness(withTenants: false);

        var ex = await Should.ThrowAsync<AggregateException>(() => harness.Daemon.CatchUpAsync(CancellationToken.None));
        ex.InnerExceptions.ShouldContain(x => x.Message == "boom");
    }

    [Fact]
    public async Task per_tenant_catch_up_throws_when_a_shard_faults_even_if_the_tracker_delivers_slowly()
    {
        await using var harness = new Harness(withTenants: true);

        var ex = await Should.ThrowAsync<AggregateException>(() => harness.Daemon.CatchUpAsync(CancellationToken.None));
        ex.InnerExceptions.ShouldContain(x => x.Message == "boom");
    }

    [Fact]
    public async Task catch_up_does_not_throw_when_no_shard_faults()
    {
        await using var harness = new Harness(withTenants: false, fault: false);

        await Should.NotThrowAsync(() => harness.Daemon.CatchUpAsync(CancellationToken.None));
    }

    private sealed class SlowObserver : IObserver<ShardState>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(ShardState value) => Thread.Sleep(750);
    }

    private sealed class FaultingExecution : ISubscriptionExecution
    {
        private readonly bool _fault;
        public FaultingExecution(ShardName name, bool fault) { ShardName = name; _fault = fault; }
        public ShardName ShardName { get; }
        public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent) => ValueTask.CompletedTask;
        public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;
        public Task HardStopAsync() => Task.CompletedTask;
        public ShardExecutionMode Mode { get; set; }
        public bool TryBuildReplayExecutor([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReplayExecutor? executor) { executor = null; return false; }

        public async Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
            CancellationToken cancellation)
        {
            // What the real executions do on a projection failure
            if (_fault) await subscriptionAgent.ReportCriticalFailureAsync(new InvalidOperationException("boom"));
        }

        public Task ProcessRangeAsync(EventRange range) => Task.CompletedTask;

        public bool TryGetAggregateCache<TId, TDoc>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
        {
            caching = null;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Detector : IHighWaterDetector
    {
        public Uri DatabaseUri { get; } = new("fake://db1");
        public bool SupportsTenantPartitioning => true;

        public Task<HighWaterStatistics> Detect(CancellationToken token)
            => Task.FromResult(new HighWaterStatistics { CurrentMark = 10, HighestSequence = 10, LastMark = 0 });

        public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

        public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken token)
            => Task.FromResult(new HighWaterVector(tenantIds.Select(t => new HighWaterStatistics
            {
                TenantId = t, CurrentMark = 10, LastMark = 0, HighestSequence = 10
            }).ToArray()));

        public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
            CancellationToken token) => DetectForTenantsAsync(tenantIds, token);
    }

    private sealed class FakeProjectionGraph :
        ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeProjectionGraph() : base(Substitute.For<IEventRegistry>(), "tests") { }
        protected override void onAddProjection(object projection) { }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>> Daemon { get; }

        public Harness(bool withTenants, bool fault = true)
        {
            var factory = Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>();
            factory.BuildExecution(Arg.Any<IEventStore<FakeOperations, FakeSession>>(), Arg.Any<IEventDatabase>(),
                    Arg.Any<ILogger>(), Arg.Any<ShardName>())
                .Returns(c => new FaultingExecution(c.Arg<ShardName>(), fault));

            var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
                new ShardName("Trip"), factory, new EventFilterable());

            var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
            store.Meter.Returns(new Meter("tests"));
            store.TimeProvider.Returns(TimeProvider.System);
            store.AutoCreateSchemaObjects.Returns(AutoCreate.None);
            store.ContinuousErrors.Returns(new ErrorHandlingOptions());
            store.RebuildErrors.Returns(new ErrorHandlingOptions());
            store.AllShards().Returns(new[] { shard });

            var loader = Substitute.For<IEventLoader>();
            loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
                .Returns(c =>
                {
                    var request = c.Arg<EventRequest>();
                    var page = new EventPage(request.Floor);
                    page.CalculateCeiling(request.BatchSize, request.HighWater);
                    return Task.FromResult(page);
                });
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>()).Returns(loader);
            store.BuildEventLoader(Arg.Any<IEventDatabase>(), Arg.Any<ILogger>(), Arg.Any<EventFilterable>(),
                Arg.Any<AsyncOptions>(), Arg.Any<ShardName>()).Returns(loader);

            IEventDatabase database;
            if (withTenants)
            {
                database = Substitute.For<IEventDatabase, ICrossTenantRebuildSource>();
                ((ICrossTenantRebuildSource)database)
                    .FindRebuildTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "t1" }));
            }
            else
            {
                database = Substitute.For<IEventDatabase>();
            }

            database.Identifier.Returns("db1");
            database.DatabaseUri.Returns(new Uri("fake://db1"));
            database.AllProjectionProgress(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ShardState>>(Array.Empty<ShardState>()));
            var tracker = new ShardStateTracker(new NulloLogger());
            database.Tracker.Returns(tracker);

            // The slow listener sits ahead of CatchUpAsync's Recorder on the tracker's single consumer
            tracker.Subscribe(new SlowObserver());

            Daemon = new JasperFxAsyncDaemon<FakeOperations, FakeSession, IJasperFxProjection<FakeOperations>>(
                store, database, new NulloLogger(), new Detector(), new FakeProjectionGraph());
        }

        public ValueTask DisposeAsync()
        {
            Daemon.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
