using JasperFx.Descriptors;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.CommandLine;

// Verifies which subscriptions the `projections rebuild` command actually feeds to the host:
//   - #4711: Live-lifecycle projections are skipped (no persisted state to rebuild).
//   - subscriptions (SubscriptionType.Subscription, e.g. Wolverine's PublishEventsToWolverine) are
//     skipped — they have no projected state, the daemon's RebuildProjectionAsync only resolves
//     PROJECTION names (so a subscription name throws "No registered projection matches..."), and a
//     rebuild would re-publish every historical event (contrary to SubscribeFromPresent()).
//   - Inline and Async PROJECTIONS remain rebuildable.
public class ProjectionRebuildSelectionTests : IProjectionHost
{
    private readonly ProjectionController theController;
    private string[] _rebuiltNames = [];

    public ProjectionRebuildSelectionTests()
    {
        theController = new ProjectionController(this, new NulloConsoleView());
    }

    private static SubscriptionDescriptor descriptor(string name, ProjectionLifecycle lifecycle, SubscriptionType type)
    {
        return new SubscriptionDescriptor(type)
        {
            Name = name,
            Lifecycle = lifecycle,
            ShardNames = [new ShardName(name)]
        };
    }

    private EventStoreUsage usageWith(params SubscriptionDescriptor[] subscriptions)
    {
        var usage = new EventStoreUsage
        {
            SubjectUri = new Uri("marten://main"),
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = new DatabaseDescriptor { Identifier = "*Default*" }
            }
        };

        usage.Subscriptions.AddRange(subscriptions);
        return usage;
    }

    [Fact]
    public async Task rebuild_all_skips_subscriptions_and_live_but_keeps_projections()
    {
        _usages =
        [
            usageWith(
                descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection),
                descriptor("InlineProjection", ProjectionLifecycle.Inline, SubscriptionType.MultiStreamProjection),
                descriptor("LiveProjection", ProjectionLifecycle.Live, SubscriptionType.SingleStreamProjection),
                descriptor("WolverineRelay", ProjectionLifecycle.Async, SubscriptionType.Subscription))
        ];

        await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild });

        _rebuiltNames.ShouldBe(["AsyncProjection", "InlineProjection"], ignoreOrder: true);
        _rebuiltNames.ShouldNotContain("WolverineRelay");
        _rebuiltNames.ShouldNotContain("LiveProjection");
    }

    [Fact]
    public async Task named_rebuild_of_a_subscription_is_skipped()
    {
        _usages =
        [
            usageWith(
                descriptor("WolverineRelay", ProjectionLifecycle.Async, SubscriptionType.Subscription))
        ];

        await theController.Execute(new ProjectionInput
        {
            Action = ProjectionAction.rebuild, ProjectionFlag = "WolverineRelay"
        });

        _rebuiltNames.ShouldBeEmpty();
    }

    #region IProjectionHost test double

    private IReadOnlyList<EventStoreUsage> _usages = [];

    public Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync() => Task.FromResult(_usages);

    public void ListenForUserTriggeredExit() { }

    public Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier,
        ProjectionInput input, string[] names, TimeSpan? shardTimeout = null)
    {
        _rebuiltNames = names;
        return Task.FromResult(RebuildStatus.Complete);
    }

    public Task StartShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier, string[] projectionNames)
        => Task.CompletedTask;

    public Task WaitForExitAsync() => Task.CompletedTask;

    public Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none)
        => Task.CompletedTask;

    #endregion
}

internal class NulloConsoleView : IConsoleView
{
    public void DisplayNoStoresMessage() { }
    public void ListShards(IReadOnlyList<EventStoreUsage> usages) { }
    public void DisplayEmptyEventsMessage(EventStoreDatabaseIdentifier usage) { }
    public void DisplayNoAsyncProjections() { }
    public void DisplayRebuildIsComplete() { }
    public void DisplayInvalidShardTimeoutValue() { }
    public void WriteStartingToRebuildProjections(ProjectionSelection selection, string databaseName) { }
}
