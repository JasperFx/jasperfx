using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// Coverage for JasperFx/ProductSupport#5: a singleton
/// <see cref="IObserver{ShardState}"/> attached to multiple daemons (one
/// per <see cref="IEventStore"/>) needs the owning store URI stamped on
/// every <see cref="ShardState"/> before it sees it. The Tracker can't do
/// that itself — multiple stores share a Tracker per database — so the
/// stamping lives one level up at the daemon-subscription boundary via
/// <see cref="StoreUriStampingObserver"/> +
/// <see cref="ProjectionDaemonExtensions.SubscribeWithStoreUriStamp"/>.
/// </summary>
public class StoreUriStampingObserverTests
{
    [Fact]
    public void stamps_store_uri_when_inner_state_has_none()
    {
        var inner = new RecordingObserver();
        var stamper = new StoreUriStampingObserver(inner, "marten://main");

        var state = new ShardState("Trip:V1:All", 42);
        stamper.OnNext(state);

        inner.States.ShouldHaveSingleItem();
        inner.States[0].StoreUri.ShouldBe("marten://main");
        // Same instance passed through — the observer doesn't clone.
        ReferenceEquals(inner.States[0], state).ShouldBeTrue();
    }

    [Fact]
    public void preserves_existing_store_uri_already_set_by_upstream()
    {
        // Belt + suspenders for a future where the daemon itself starts
        // pre-stamping states; the decorator should not overwrite a value
        // an upstream layer has already put on.
        var inner = new RecordingObserver();
        var stamper = new StoreUriStampingObserver(inner, "marten://main");

        var state = new ShardState("Trip:V1:All", 42) { StoreUri = "marten://upstream-set" };
        stamper.OnNext(state);

        inner.States[0].StoreUri.ShouldBe("marten://upstream-set");
    }

    [Fact]
    public void forwards_completion_and_error_callbacks()
    {
        var inner = new RecordingObserver();
        var stamper = new StoreUriStampingObserver(inner, "marten://main");

        stamper.OnError(new InvalidOperationException("boom"));
        stamper.OnCompleted();

        inner.LastError.ShouldBeOfType<InvalidOperationException>();
        inner.CompletionCount.ShouldBe(1);
    }

    [Fact]
    public void subscribe_with_store_uri_stamp_routes_through_tracker_and_stamps()
    {
        var tracker = new ShardStateTracker(new NulloLogger());
        var inner = new RecordingObserver();

        var daemon = Substitute.For<IProjectionDaemon>();
        daemon.Tracker.Returns(tracker);
        daemon.StoreUri.Returns("marten://itarievenstore");

        using var subscription = daemon.SubscribeWithStoreUriStamp(inner);

        // Publish through the real Tracker; the stamper should run before
        // `inner` sees the state.
        var task = tracker.PublishAsync(new ShardState("Trip:V1:All", 7)).AsTask();
        task.Wait();
        tracker.Complete().Wait();

        inner.States.ShouldHaveSingleItem();
        inner.States[0].StoreUri.ShouldBe("marten://itarievenstore",
            "Daemon's StoreUri must ride through to the inner observer.");

        tracker.As<IDisposable>().Dispose();
    }

    [Fact]
    public void subscribe_with_store_uri_stamp_falls_back_to_direct_subscription_when_daemon_has_no_uri()
    {
        // Test scaffolding / very old client: StoreUri is null. The extension
        // should still hook the observer up so legacy callers don't lose
        // shard-state notifications — just unstamped.
        var tracker = new ShardStateTracker(new NulloLogger());
        var inner = new RecordingObserver();

        var daemon = Substitute.For<IProjectionDaemon>();
        daemon.Tracker.Returns(tracker);
        daemon.StoreUri.Returns((string?)null);

        using var subscription = daemon.SubscribeWithStoreUriStamp(inner);

        tracker.PublishAsync(new ShardState("Trip:V1:All", 7)).AsTask().Wait();
        tracker.Complete().Wait();

        inner.States.ShouldHaveSingleItem();
        inner.States[0].StoreUri.ShouldBeNull();

        tracker.As<IDisposable>().Dispose();
    }

    private sealed class RecordingObserver : IObserver<ShardState>
    {
        public readonly List<ShardState> States = new();
        public Exception? LastError;
        public int CompletionCount;

        public void OnNext(ShardState value) => States.Add(value);
        public void OnError(Exception error) => LastError = error;
        public void OnCompleted() => CompletionCount++;
    }
}
