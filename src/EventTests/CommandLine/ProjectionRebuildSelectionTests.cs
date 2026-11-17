using JasperFx.Descriptors;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon.HighWater;
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
        => usageWith(new Uri("marten://main"), subscriptions);

    private EventStoreUsage usageWith(Uri subjectUri, params SubscriptionDescriptor[] subscriptions)
    {
        var usage = new EventStoreUsage
        {
            SubjectUri = subjectUri,
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

    // jasperfx#1210: --advance was parsed and then ignored

    [Fact]
    public async Task advance_moves_the_high_water_mark_before_a_rebuild()
    {
        theHost.Usages =
        [
            usageWith(descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];

        (await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild, AdvanceFlag = true }))
            .ShouldBeTrue();

        theHost.Calls.ShouldBe(["advance main", "rebuild main"]);
        theHost.AdvancedSelections.Single().DatabaseIdentifiers.ShouldBe(["*Default*"]);
    }

    [Fact]
    public async Task advance_moves_the_high_water_mark_before_running_continuously()
    {
        theHost.Usages =
        [
            usageWith(descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];

        (await theController.Execute(new ProjectionInput { Action = ProjectionAction.run, AdvanceFlag = true }))
            .ShouldBeTrue();

        theHost.Calls.ShouldBe(["advance main", "start main"]);
    }

    [Fact]
    public async Task without_advance_no_high_water_mark_moves()
    {
        theHost.Usages =
        [
            usageWith(descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];

        await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild });

        theHost.AdvancedSelections.ShouldBeEmpty();
        theHost.Calls.ShouldBe(["rebuild main"]);
    }

    [Fact]
    public async Task advance_is_not_applied_to_the_list_action()
    {
        theHost.Usages =
        [
            usageWith(descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];

        (await theController.Execute(new ProjectionInput { Action = ProjectionAction.list, AdvanceFlag = true }))
            .ShouldBeTrue();

        theHost.AdvancedSelections.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_failed_advance_stops_before_anything_is_rebuilt()
    {
        theHost.Usages =
        [
            usageWith(descriptor("AsyncProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];
        theHost.AdvanceFailure = new NotSupportedException("this store cannot advance its high water mark");

        (await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild, AdvanceFlag = true }))
            .ShouldBeFalse();

        theHost.Calls.ShouldBe(["advance main"]);
    }

    [Fact]
    public async Task advance_and_rebuild_cover_every_selected_event_store()
    {
        // The rebuild used to return after the first event store, so a second one was never rebuilt
        theHost.Usages =
        [
            usageWith(new Uri("marten://main"),
                descriptor("MainProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection)),
            usageWith(new Uri("marten://other"),
                descriptor("OtherProjection", ProjectionLifecycle.Async, SubscriptionType.SingleStreamProjection))
        ];

        (await theController.Execute(new ProjectionInput { Action = ProjectionAction.rebuild, AdvanceFlag = true }))
            .ShouldBeTrue();

        theHost.Calls.ShouldBe([
            "advance main", "advance other", "rebuild main", "rebuild other"
        ]);
    }

    [Fact]
    public void the_advance_report_names_the_mark_and_what_was_skipped()
    {
        ProjectionHost.DescribeAdvance(new HighWaterMarkAdvance("main", null, HighWaterAdvanceOutcome.Advanced, 10, 15, 2))
            .ShouldBe("High water mark of database 'main' advanced from 10 to 15, skipping 2 sequence number(s) with no committed event");

        ProjectionHost.DescribeAdvance(new HighWaterMarkAdvance("main", "red", HighWaterAdvanceOutcome.AlreadyAtCeiling, 15, 15, 0))
            .ShouldBe("High water mark of tenant 'red' in database 'main' is already at the latest committed sequence (15)");
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

    // The order the controller called the host in, one entry per call: "advance", "rebuild" or "start" plus the store
    public List<string> Calls { get; } = [];

    public List<ProjectionSelection> AdvancedSelections { get; } = [];

    public Exception? AdvanceFailure { get; set; }

    public Task<IReadOnlyList<EventStoreUsage>> AllStoresAsync() => Task.FromResult(Usages);

    public void ListenForUserTriggeredExit() { }

    public Task<RebuildStatus> TryRebuildShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier,
        ProjectionInput input, string[] names, TimeSpan? shardTimeout = null)
    {
        RebuiltNames = names;
        Calls.Add($"rebuild {databaseIdentifier.SubjectUri.Host}");
        return Task.FromResult(RebuildStatus.Complete);
    }

    public Task StartShardsAsync(EventStoreDatabaseIdentifier databaseIdentifier, string[] projectionNames)
    {
        Calls.Add($"start {databaseIdentifier.SubjectUri.Host}");
        return Task.CompletedTask;
    }

    public Task WaitForExitAsync() => Task.CompletedTask;

    public Task AdvanceHighWaterMarkToLatestAsync(ProjectionSelection selection, CancellationToken none)
    {
        Calls.Add($"advance {selection.Storage.SubjectUri.Host}");
        AdvancedSelections.Add(selection);

        return AdvanceFailure == null ? Task.CompletedTask : Task.FromException(AdvanceFailure);
    }
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
