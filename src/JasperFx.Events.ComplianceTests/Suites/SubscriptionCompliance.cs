using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Subscription events

public record BeaconLit(string Name);

public record BeaconDimmed(int Level);

public record BeaconExtinguished;

/// <summary>
/// The portable half of the shared compliance subscription: everything about recording what the
/// daemon delivered, and waiting for it.
/// </summary>
/// <remarks>
/// <para>
/// This is the second shared type in the library that cannot be reached by an alias alone — the
/// same shape as <c>ComplianceFlatTableProjection</c>, and for the same reason. Both products
/// declare their own <c>ISubscription</c> with an identical member,
/// <c>Task&lt;IChangeListener&gt; ProcessEventsAsync(EventRange, ISubscriptionController,
/// IDocumentOperations, CancellationToken)</c> — but <c>IChangeListener</c> is per-product, so the
/// signature cannot be written once. Each consumer supplies a small partial implementing its own
/// interface and calling <see cref="Record"/>.
/// </para>
/// <para>
/// Recording is under a lock because the daemon delivers pages from its own threads. An
/// unsynchronized <c>List.Add</c> here would be the marten#5085 class of bug — a test-side data
/// race that presents as an impossible assertion failure rather than as a race.
/// </para>
/// </remarks>
public partial class ComplianceSubscription
{
    /// <summary>
    /// The daemon-facing name, pinned rather than defaulted — the products disagree on whether an
    /// unnamed subscription takes its short type name or its full name, and progression is keyed
    /// on it.
    /// </summary>
    public const string SubscriptionName = "ComplianceSubscription";

    private readonly object _lock = new();
    private readonly List<IEvent> _received = new();
    private int _pageCount;

    public IReadOnlyList<IEvent> Received
    {
        get
        {
            lock (_lock)
            {
                return _received.ToArray();
            }
        }
    }

    public int PageCount
    {
        get
        {
            lock (_lock)
            {
                return _pageCount;
            }
        }
    }

    /// <summary>
    /// Called by each consumer's partial from its own <c>ProcessEventsAsync</c>.
    /// </summary>
    protected void Record(IEnumerable<IEvent> events)
    {
        lock (_lock)
        {
            _received.AddRange(events);
            _pageCount++;
        }
    }

    /// <summary>
    /// Poll until at least <paramref name="count"/> events from <paramref name="streamId"/> have
    /// arrived, or give up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped to one stream on purpose. A suite cannot wait on a store-wide total, because this
    /// subscription instance is shared across the suite's tests and earlier tests' events may still
    /// be in flight — a total-count latch would be satisfied by the wrong events.
    /// </para>
    /// <para>
    /// And it cannot wait on "non-stale projection data" either: that tracks *projections*, and a
    /// store configured with a subscription and no projections can report non-stale before the
    /// subscription has been handed anything at all. Waiting on the subscription's own delivery is
    /// the only signal that means what these tests need.
    /// </para>
    /// <para>
    /// Polling rather than a <c>TaskCompletionSource</c> because the assertions are about how many
    /// events arrive, and a latch on a count cannot also show that no further ones followed.
    /// </para>
    /// </remarks>
    public async Task WaitForStreamEventCountAsync(Guid streamId, int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Received.Count(x => x.StreamId == streamId) >= count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for {count} events from stream {streamId}; " +
            $"the subscription received {Received.Count(x => x.StreamId == streamId)} from that stream " +
            $"and {Received.Count} in total.");
    }
}

#endregion

/// <summary>
/// Subscriptions — the "do something with every event, in order, exactly once" surface that is not
/// a projection.
/// </summary>
/// <remarks>
/// <para>
/// The guarantees worth pinning are ordering and completeness: a subscription sees every event the
/// store appended, in sequence order, across however many pages the daemon chooses to deliver. Page
/// boundaries are an implementation detail and are deliberately never asserted — only that the
/// union of the pages is right and monotonic.
/// </para>
/// <para>
/// Cost is a per-consumer partial on <see cref="ComplianceSubscription"/> plus one registrar
/// member. That is more than most suites and it is the honest price: neither product exposes a
/// public <c>Subscribe</c> overload taking the shared <c>ISubscriptionSource&lt;TOperations,
/// TQuerySession&gt;</c>, even though both prefer it internally, and their
/// <c>registerSubscription</c> is private in both (marten#5151).
/// </para>
/// </remarks>
public abstract class SubscriptionCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One subscription instance for the whole suite, because the configuration delegate is what
    /// the fixture keys a store rebuild on — a new instance per test would need a new delegate.
    /// Tests therefore assert on what arrived for the streams they appended, never on a total.
    /// </summary>
    private static readonly ComplianceSubscription _subscription = new();

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_subscriptions";

        config.AddEventType<BeaconLit>();
        config.AddEventType<BeaconDimmed>();
        config.AddEventType<BeaconExtinguished>();

        config.Subscribe(_subscription);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    private async Task<Guid> aBeaconAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            events.Length == 0 ? [new BeaconLit("Amon Din")] : events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private IReadOnlyList<IEvent> receivedFor(Guid streamId)
        => _subscription.Received.Where(x => x.StreamId == streamId).ToArray();

    [Fact]
    public async Task a_subscription_receives_the_events_that_were_appended()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Amon Din"), new BeaconDimmed(3),
            new BeaconExtinguished());

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 3, _timeout);

        receivedFor(streamId).Count.ShouldBe(3);
    }

    [Fact]
    public async Task the_events_arrive_with_their_data_intact()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Eilenach"), new BeaconDimmed(7));

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 2, _timeout);

        var received = receivedFor(streamId);

        received.Select(x => x.Data).OfType<BeaconLit>().Single().Name.ShouldBe("Eilenach");
        received.Select(x => x.Data).OfType<BeaconDimmed>().Single().Level.ShouldBe(7);
    }

    [Fact]
    public async Task the_events_arrive_in_sequence_order()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Nardol"), new BeaconDimmed(1),
            new BeaconDimmed(2), new BeaconExtinguished());

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 4, _timeout);

        var received = receivedFor(streamId);
        received.Count.ShouldBe(4);

        // Monotonic on both axes, whatever page boundaries the daemon chose.
        received.Select(x => x.Sequence).ShouldBe(received.Select(x => x.Sequence).OrderBy(x => x));
        received.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task a_subscription_sees_events_from_every_stream()
    {
        SkipUnlessDaemonIsSupported();

        var first = await aBeaconAsync(new BeaconLit("Erelas"), new BeaconDimmed(1));
        var second = await aBeaconAsync(new BeaconLit("Min-Rimmon"));
        var third = await aBeaconAsync(new BeaconLit("Calenhad"), new BeaconExtinguished());

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(first, 2, _timeout);
        await _subscription.WaitForStreamEventCountAsync(second, 1, _timeout);
        await _subscription.WaitForStreamEventCountAsync(third, 2, _timeout);

        receivedFor(first).Count.ShouldBe(2);
        receivedFor(second).Count.ShouldBe(1);
        receivedFor(third).Count.ShouldBe(2);
    }

    [Fact]
    public async Task events_appended_after_the_daemon_started_still_arrive()
    {
        SkipUnlessDaemonIsSupported();

        await StartDaemonAsync();

        // Appended only after the daemon is already running, so this is catch-up-from-live rather
        // than the cold replay every other test in this suite exercises.
        var streamId = await aBeaconAsync(new BeaconLit("Halifirien"), new BeaconDimmed(9));

        await _subscription.WaitForStreamEventCountAsync(streamId, 2, _timeout);

        receivedFor(streamId).Count.ShouldBe(2);
    }

    [Fact]
    public async Task each_event_is_delivered_once()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Amon Anwar"), new BeaconDimmed(4),
            new BeaconExtinguished());

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 3, _timeout);

        var received = receivedFor(streamId);

        // Redelivery is the failure this catches: a duplicate page would show up as repeated
        // sequences rather than as a wrong total, since the count assertion alone would pass if a
        // page were dropped AND another duplicated.
        received.Select(x => x.Sequence).Distinct().Count().ShouldBe(received.Count);
        received.Count.ShouldBe(3);
    }
}
