using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

// jasperfx#407 Phase A: composite single-pass rebuild wiring + membership guard.
public class CompositeProjectionReplayTests
{
    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();

    private static IProjectionSource<FakeOperations, FakeSession> AsSource(
        CompositeProjection<FakeOperations, FakeSession> composite) => composite;

    [Fact]
    public void empty_composite_is_not_eligible_for_single_pass_replay()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.IsEligibleForReplay().ShouldBeFalse();
    }

    [Fact]
    public void eligible_when_every_member_can_participate()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));
        composite.StageFor(2).Add(new FakeReplayMember("B", canParticipate: true));

        composite.IsEligibleForReplay().ShouldBeTrue();
    }

    [Fact]
    public void not_eligible_when_any_member_is_custom_sliced()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));
        composite.StageFor(1).Add(new FakeReplayMember("CustomSliced", canParticipate: false));

        composite.IsEligibleForReplay().ShouldBeFalse();
    }

    [Fact]
    public void source_declines_replay_executor_when_a_member_cannot_participate()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.StageFor(1).Add(new FakeReplayMember("CustomSliced", canParticipate: false));

        AsSource(composite).TryBuildReplayExecutor(theStore, theDatabase, out var executor).ShouldBeFalse();
        executor.ShouldBeNull();
    }

    [Fact]
    public void source_builds_a_composite_replay_executor_when_eligible()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.StageFor(1).Add(new FakeReplayMember("A", canParticipate: true));
        composite.StageFor(2).Add(new FakeReplayMember("B", canParticipate: true));

        AsSource(composite).TryBuildReplayExecutor(theStore, theDatabase, out var executor).ShouldBeTrue();
        executor.ShouldBeOfType<CompositeReplayExecutor>();
    }

    [Fact]
    public void execution_builds_replay_executor_when_eligible()
    {
        var execution = buildExecution(replayEligible: true);
        execution.TryBuildReplayExecutor(out var executor).ShouldBeTrue();
        executor.ShouldBeOfType<CompositeReplayExecutor>();
    }

    [Fact]
    public void execution_declines_replay_executor_when_not_eligible()
    {
        var execution = buildExecution(replayEligible: false);
        execution.TryBuildReplayExecutor(out var executor).ShouldBeFalse();
        executor.ShouldBeNull();
    }

    [Fact]
    public async Task single_pass_fans_one_page_to_every_member_and_commits_one_batch()
    {
        // The heart of Phase A: one event range, read once, dispatched to all member stages, committed
        // together as a single batch. (Document-level correctness is asserted in marten#4596.)
        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        batch.RecordProgress(Arg.Any<EventRange>()).Returns(ValueTask.CompletedTask);

        theStore.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));
        theStore.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(new ErrorHandlingOptions { SkipApplyErrors = false });

        var member1 = buildMemberExecution("Member1");
        var member2 = buildMemberExecution("Member2");
        var member3 = buildMemberExecution("Member3");

        // Two client-defined stage levels: stage 1 has two members, stage 2 has one.
        var stages = new[]
        {
            new ExecutionStage([member1, member2]),
            new ExecutionStage([member3])
        };

        var execution = new CompositeExecution<FakeOperations, FakeSession>(
            new ShardName("Composite", ShardName.All, 0), new AsyncOptions(), theStore, theDatabase,
            Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance, stages)
        {
            Mode = ShardExecutionMode.Rebuild
        };

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var range = new EventRange(agent, 0, 5)
        {
            Events = "ab".ToLetterEventsWithWrapper().ToList()
        };

        await execution.ProcessRangeAsync(range);

        // Each member saw the page exactly once -> single pass, not once-per-member
        await member1.Received(1).ProcessRangeAsync(Arg.Any<EventRange>());
        await member2.Received(1).ProcessRangeAsync(Arg.Any<EventRange>());
        await member3.Received(1).ProcessRangeAsync(Arg.Any<EventRange>());

        // One combined batch, committed once -> all members commit together
        await theStore.Received(1).StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
            Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>());
        await batch.Received(1).ExecuteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task members_inherit_the_composite_mode_so_rebuilds_suppress_side_effects()
    {
        // marten#4729: member executions default to ShardExecutionMode.Continuous and are never set by
        // CompositeReplayExecutor (which only assigns the parent's Mode). The optimized composite rebuild
        // therefore ran members in Continuous and fired their side effects (RaiseSideEffects ->
        // PublishMessage/AppendEvent), unlike the classic single-stream rebuild. CompositeExecution must
        // propagate its Mode down to every member as part of building the batch.
        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        batch.RecordProgress(Arg.Any<EventRange>()).Returns(ValueTask.CompletedTask);

        theStore.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));
        theStore.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>())
            .Returns(new ErrorHandlingOptions { SkipApplyErrors = false });

        var member1 = buildMemberExecution("Member1");
        var member2 = buildMemberExecution("Member2");
        var member3 = buildMemberExecution("Member3");

        var stages = new[]
        {
            new ExecutionStage([member1, member2]),
            new ExecutionStage([member3])
        };

        var execution = new CompositeExecution<FakeOperations, FakeSession>(
            new ShardName("Composite", ShardName.All, 0), new AsyncOptions(), theStore, theDatabase,
            Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance, stages)
        {
            Mode = ShardExecutionMode.Rebuild
        };

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var range = new EventRange(agent, 0, 5)
        {
            Events = "ab".ToLetterEventsWithWrapper().ToList()
        };

        await execution.ProcessRangeAsync(range);

        // Every member ran in the composite's mode, so AggregationRunner suppresses side effects in a rebuild
        member1.Mode.ShouldBe(ShardExecutionMode.Rebuild);
        member2.Mode.ShouldBe(ShardExecutionMode.Rebuild);
        member3.Mode.ShouldBe(ShardExecutionMode.Rebuild);
    }

    [Fact]
    public void member_stages_inherit_a_store_global_parent_binding()
    {
        // jasperfx#419: with no tenant on the parent composite shard, member stages stay store-global,
        // byte-for-byte with the pre-refactor behavior.
        var stage = new ProjectionStage<FakeOperations, FakeSession>(1);
        stage.Add(new FakeReplayMember("A", canParticipate: true));
        stage.Add(new FakeReplayMember("B", canParticipate: true));

        var parent = ShardName.Compose("Composite", version: 2);
        var executionStage = stage.BuildExecution(theStore, theDatabase, NullLogger.Instance, parent);

        foreach (var execution in executionStage.Executions)
        {
            execution.ShardName.TenantId.ShouldBeNull();
        }
    }

    [Fact]
    public void member_stages_inherit_the_parent_tenant_binding()
    {
        // jasperfx#419 root cause of marten#4679: when the composite is caught up/rebuilt for a single
        // tenant, the parent ShardName carries that tenant id. Each member stage's ShardName MUST inherit
        // it -- otherwise every per-tenant member writes the same store-global mt_event_progression row
        // and the second tenant's INSERT trips 23505.
        var stage = new ProjectionStage<FakeOperations, FakeSession>(1);
        stage.Add(new FakeReplayMember("A", canParticipate: true));
        stage.Add(new FakeReplayMember("B", canParticipate: true));

        var parent = ShardName.Compose("Composite", tenantId: "tenant1", version: 2);
        var executionStage = stage.BuildExecution(theStore, theDatabase, NullLogger.Instance, parent);

        executionStage.Executions.Select(x => x.ShardName.Identity)
            .ShouldBe(["A:All:tenant1", "B:All:tenant1"]);
    }

    private CompositeExecution<FakeOperations, FakeSession> buildExecution(bool replayEligible)
        => new(new ShardName("Composite", ShardName.All, 0), new AsyncOptions(), theStore, theDatabase,
            Substitute.For<IJasperFxProjection<FakeOperations>>(), NullLogger.Instance,
            Array.Empty<ExecutionStage>(), replayEligible);

    private static ISubscriptionExecution buildMemberExecution(string name)
    {
        var execution = Substitute.For<ISubscriptionExecution>();
        execution.ShardName.Returns(new ShardName(name));
        execution.CompactCachesAsync().Returns(Task.CompletedTask);
        return execution;
    }
}

// Concrete fake (NSubstitute cannot stub the default-interface member CanParticipateInCompositeReplay).
// Only Name + CanParticipateInCompositeReplay are exercised by the guard; BuildExecution backs the
// eligible source-build path.
internal class FakeReplayMember : IProjectionSource<FakeOperations, FakeSession>,
    ISubscriptionFactory<FakeOperations, FakeSession>
{
    public FakeReplayMember(string name, bool canParticipate)
    {
        Name = name;
        CanParticipateInCompositeReplay = canParticipate;
    }

    public string Name { get; }
    public uint Version => 1;
    public SubscriptionType Type => SubscriptionType.SingleStreamProjection;
    public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
    public Type ImplementationType => GetType();
    public AsyncOptions Options { get; } = new();
    public bool CanParticipateInCompositeReplay { get; }

    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];

    public SubscriptionDescriptor Describe(IEventStore store) => throw new NotSupportedException();

    public IReadOnlyList<AsyncShard<FakeOperations, FakeSession>> Shards() => throw new NotSupportedException();

    public bool TryBuildReplayExecutor(IEventStore<FakeOperations, FakeSession> store, IEventDatabase database,
        out IReplayExecutor? executor)
    {
        executor = null;
        return false;
    }

    public IInlineProjection<FakeOperations> BuildForInline() => throw new NotSupportedException();

    public IEnumerable<Type> PublishedTypes() => [];

    public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
        => BuildExecution(store, database, NullLogger.Instance, shardName);

    public ISubscriptionExecution BuildExecution(IEventStore<FakeOperations, FakeSession> store,
        IEventDatabase database, ILogger logger, ShardName shardName)
    {
        var execution = Substitute.For<ISubscriptionExecution>();
        execution.ShardName.Returns(shardName);
        return execution;
    }
}
