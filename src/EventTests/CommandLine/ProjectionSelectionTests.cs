using JasperFx.Events;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.CommandLine;

// marten#4718 backport: `projections rebuild` must skip event subscriptions and
// Live-lifecycle projections so a registered subscription's name never reaches
// RebuildProjectionAsync -> TryFindProjection (which only searches projections
// and throws "No registered projection matches the name '...'").
public class ProjectionSelectionTests
{
    private static SubscriptionDescriptor Descriptor(string name, SubscriptionType type, ProjectionLifecycle lifecycle)
        => new(type) { Name = name, Lifecycle = lifecycle };

    private static ProjectionSelection SelectionWith(params SubscriptionDescriptor[] descriptors)
    {
        var selection = new ProjectionSelection(new EventStoreUsage(new Uri("marten://main"), new object()));
        selection.Subscriptions.AddRange(descriptors);
        return selection;
    }

    [Fact]
    public void rebuildable_excludes_event_subscriptions()
    {
        var selection = SelectionWith(
            Descriptor("RealProjection", SubscriptionType.SingleStreamProjection, ProjectionLifecycle.Async),
            Descriptor("DriverSupportTeamEvents", SubscriptionType.Subscription, ProjectionLifecycle.Async));

        var names = selection.RebuildableSubscriptions().Select(x => x.Name).ToArray();

        names.ShouldBe(new[] { "RealProjection" });
    }

    [Fact]
    public void rebuildable_excludes_live_projections()
    {
        var selection = SelectionWith(
            Descriptor("AsyncProjection", SubscriptionType.MultiStreamProjection, ProjectionLifecycle.Async),
            Descriptor("LiveProjection", SubscriptionType.SingleStreamProjection, ProjectionLifecycle.Live));

        var names = selection.RebuildableSubscriptions().Select(x => x.Name).ToArray();

        names.ShouldBe(new[] { "AsyncProjection" });
    }

    [Fact]
    public void rebuildable_keeps_async_and_inline_projections_including_composites()
    {
        var selection = SelectionWith(
            Descriptor("Async", SubscriptionType.SingleStreamProjection, ProjectionLifecycle.Async),
            Descriptor("Inline", SubscriptionType.EventProjection, ProjectionLifecycle.Inline),
            Descriptor("Composite", SubscriptionType.CompositeProjection, ProjectionLifecycle.Async));

        var names = selection.RebuildableSubscriptions().Select(x => x.Name).ToArray();

        names.ShouldBe(new[] { "Async", "Inline", "Composite" });
    }

    [Fact]
    public void rebuildable_is_empty_when_only_a_subscription_is_registered()
    {
        // A subscription name passed explicitly to rebuild becomes a clean no-op.
        var selection = SelectionWith(
            Descriptor("OnlySubscription", SubscriptionType.Subscription, ProjectionLifecycle.Async));

        selection.RebuildableSubscriptions().ShouldBeEmpty();
    }
}
