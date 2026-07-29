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
public class ProjectionRebuildSelectionTests
{
    private readonly ProjectionController theController;
    private readonly RecordingProjectionHost theHost = new();

    public ProjectionRebuildSelectionTests()
    {
        theController = new ProjectionController(theHost, new NulloConsoleView());
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
        theHost.Usages =
        [
            usageWith(
                descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection),
                descriptor("InlineProjection", ProjectionLifecycle.Inline, SubscriptionType.MultiStreamProjection),
                descriptor("LiveProjection", ProjectionLifecycle.Live, SubscriptionType.SingleStreamProjection),
                descriptor("WolverineRelay", ProjectionLifecycle.Async, SubscriptionType.Subscription))
        ];

        await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild });

        theHost.RebuiltNames.ShouldBe(["AsyncProjection", "InlineProjection"], ignoreOrder: true);
        theHost.RebuiltNames.ShouldNotContain("WolverineRelay");
        theHost.RebuiltNames.ShouldNotContain("LiveProjection");
    }

    [Fact]
    public async Task named_rebuild_of_a_subscription_is_skipped()
    {
        theHost.Usages =
        [
            usageWith(
                descriptor("WolverineRelay", ProjectionLifecycle.Async, SubscriptionType.Subscription))
        ];

        await theController.Execute(new ProjectionInput
        {
            Action = ProjectionAction.rebuild, ProjectionFlag = "WolverineRelay"
        });

        theHost.RebuiltNames.ShouldBeEmpty();
    }
}

// Was implemented by the test class itself. Interface members have to be public, so every one of
// them read as a public attribute-less method on a test class (xUnit1013) -- the same self-stub
// shape that broke all 16 SliceGroupTests in #577, where a self-stubbed IAsyncDisposable ran as
// test lifecycle instead of as a stub. Splitting it out removes the warnings and the footgun.
internal class RecordingProjectionHost : IProjectionHost
{
    public IReadOnlyList<EventStoreUsage> Usages { get; set; } = [];

    public string[] RebuiltNames { get; private set; } = [];

    public Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync() => Task.FromResult(Usages);

    public void ListenForUserTriggeredExit() { }

    public Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier,
        ProjectionInput input, string[] names, TimeSpan? shardTimeout = null)
    {
        RebuiltNames = names;
        return Task.FromResult(RebuildStatus.Complete);
    }

    public Task StartShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier, string[] projectionNames)
        => Task.CompletedTask;

    public Task WaitForExitAsync() => Task.CompletedTask;

    public Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none)
        => Task.CompletedTask;
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
