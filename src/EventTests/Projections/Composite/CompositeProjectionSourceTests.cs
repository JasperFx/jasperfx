using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

public class CompositeProjectionSourceTests
{
    [Fact]
    public void wraps_a_bare_projection_with_defaults()
    {
        var source = new CompositeProjectionSource<FakeOps, FakeQuerySession>(new BareProjection());

        source.Name.ShouldBe(nameof(BareProjection));
        source.Version.ShouldBe(1u);
        source.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        ((ISubscriptionSource)source).Type.ShouldBe(SubscriptionType.EventProjection);
        source.ImplementationType.ShouldBe(typeof(BareProjection));

        // A raw projection that is not a ProjectionBase declares neither storage nor teardown,
        // so there is nothing to adopt.
        source.PublishedTypes().ShouldBeEmpty();
    }

    [Fact]
    public void reads_the_projection_version_attribute()
    {
        var source = new CompositeProjectionSource<FakeOps, FakeQuerySession>(new VersionedProjection());

        source.Version.ShouldBe(3u);
    }

    [Fact]
    public void adopts_options_and_published_types_from_a_projection_base_member()
    {
        // polecat#439 / marten#5175 / fisher#63: without adopting the member's options and published
        // types, a composite rebuild's teardown queues nothing for this member and the rebuild
        // replays onto the previous run's surviving rows.
        var projection = new ProjectionBaseMember();

        var source = new CompositeProjectionSource<FakeOps, FakeQuerySession>(projection);

        source.Options.ShouldBeSameAs(projection.Options);
        source.PublishedTypes().ShouldBe([typeof(FakeView)]);

        // Name and Version are deliberately NOT adopted: they compose this member's ShardName.Identity,
        // and changing them would orphan every existing progression row.
        source.Name.ShouldBe(nameof(ProjectionBaseMember));
        source.Version.ShouldBe(1u);
    }

    [Fact]
    public void shard_names_are_the_projection_name_with_the_all_key()
    {
        var source = new CompositeProjectionSource<FakeOps, FakeQuerySession>(new BareProjection());

        var shardName = source.ShardNames().Single();
        shardName.Name.ShouldBe(nameof(BareProjection));
        shardName.ShardKey.ShouldBe(ShardName.All);

        var shard = source.Shards().Single();
        shard.Name.Identity.ShouldBe(shardName.Identity);
        shard.Options.ShouldBeSameAs(source.Options);
    }

    [Fact]
    public void does_not_support_inline_execution()
    {
        IProjectionSource<FakeOps, FakeQuerySession> source =
            new CompositeProjectionSource<FakeOps, FakeQuerySession>(new BareProjection());

        Should.Throw<NotSupportedException>(() => source.BuildForInline());
    }

    [Fact]
    public void builds_no_replay_executor()
    {
        IProjectionSource<FakeOps, FakeQuerySession> source =
            new CompositeProjectionSource<FakeOps, FakeQuerySession>(new BareProjection());

        source.TryBuildReplayExecutor(null!, null!, out var executor).ShouldBeFalse();
        executor.ShouldBeNull();
    }

    [Fact]
    public async Task execution_applies_events_per_tenant_through_the_batch_session()
    {
        var projection = new RecordingProjection();
        var execution = new CompositeProjectionSourceExecution<FakeOps, FakeQuerySession>(
            projection, ShardName.Compose("Recording"));

        var batch = new RecordingBatch();
        var range = new EventRange(ShardName.Compose("Cmp"), 0, 3, Substitute.For<ISubscriptionAgent>())
        {
            Events =
            [
                new Event<AEvent>(new AEvent()) { TenantId = "t1" },
                new Event<AEvent>(new AEvent()) { TenantId = "t2" },
                new Event<AEvent>(new AEvent()) { TenantId = "t1" }
            ],
            ActiveBatch = batch
        };

        await execution.ProcessRangeAsync(range);

        projection.Applied.Count.ShouldBe(2);
        projection.Applied[batch.Sessions["t1"]].Count.ShouldBe(2);
        projection.Applied[batch.Sessions["t2"]].Count.ShouldBe(1);
    }

    [Fact]
    public async Task execution_does_not_dispose_the_shared_batch_but_does_dispose_its_sessions()
    {
        // THE invariant: every stage of a composite writes into one batch so the whole composite
        // commits together. A stage disposing the batch it is handed would commit the earlier stages
        // and leave the later ones writing into a disposed session.
        var execution = new CompositeProjectionSourceExecution<FakeOps, FakeQuerySession>(
            new RecordingProjection(), ShardName.Compose("Recording"));

        var batch = new RecordingBatch();
        var range = new EventRange(ShardName.Compose("Cmp"), 0, 1, Substitute.For<ISubscriptionAgent>())
        {
            Events = [new Event<AEvent>(new AEvent()) { TenantId = "t1" }],
            ActiveBatch = batch
        };

        await execution.ProcessRangeAsync(range);

        batch.WasDisposed.ShouldBeFalse();
        batch.Sessions["t1"].WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task execution_is_a_no_op_without_a_matching_active_batch()
    {
        var projection = new RecordingProjection();
        var execution = new CompositeProjectionSourceExecution<FakeOps, FakeQuerySession>(
            projection, ShardName.Compose("Recording"));

        var range = new EventRange(ShardName.Compose("Cmp"), 0, 1, Substitute.For<ISubscriptionAgent>())
        {
            Events = [new Event<AEvent>(new AEvent())],
            ActiveBatch = null
        };

        await execution.ProcessRangeAsync(range);

        projection.Applied.ShouldBeEmpty();
    }

    public class FakeQuerySession;

    public class FakeOps : FakeQuerySession, IStorageOperations
    {
        public bool WasDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return new ValueTask();
        }

        public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public bool EnableSideEffectsOnInlineProjections => false;

        public ValueTask<IMessageSink> GetOrStartMessageSink()
        {
            throw new NotSupportedException();
        }
    }

    public class BareProjection : IJasperFxProjection<FakeOps>
    {
        public Task ApplyAsync(FakeOps operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
        {
            return Task.CompletedTask;
        }
    }

    [ProjectionVersion(3)]
    public class VersionedProjection : BareProjection;

    public class FakeView;

    public class ProjectionBaseMember : ProjectionBase, IJasperFxProjection<FakeOps>
    {
        public ProjectionBaseMember()
        {
            RegisterPublishedType(typeof(FakeView));
        }

        public Task ApplyAsync(FakeOps operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
        {
            return Task.CompletedTask;
        }
    }

    public class RecordingProjection : IJasperFxProjection<FakeOps>
    {
        public Dictionary<FakeOps, List<IEvent>> Applied { get; } = new();

        public Task ApplyAsync(FakeOps operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
        {
            Applied[operations] = events.ToList();
            return Task.CompletedTask;
        }
    }

    public class RecordingBatch : IProjectionBatch<FakeOps, FakeQuerySession>
    {
        public Dictionary<string, FakeOps> Sessions { get; } = new();
        public bool WasDisposed { get; private set; }

        public FakeOps SessionForTenant(string tenantId)
        {
            var session = new FakeOps();
            Sessions[tenantId] = session;
            return session;
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return new ValueTask();
        }

        public Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;

        public void QuickAppendEventWithVersion(StreamAction action, IEvent @event)
        {
        }

        public void UpdateStreamVersion(StreamAction action)
        {
        }

        public void QuickAppendEvents(StreamAction action)
        {
        }

        public Task PublishMessageAsync(object message, string tenantId) => Task.CompletedTask;

        public ValueTask RecordProgress(EventRange range) => new();
    }
}
