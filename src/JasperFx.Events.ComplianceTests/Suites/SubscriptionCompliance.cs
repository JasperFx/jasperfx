using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Subscription events

public record BeaconLit(string Name);

public record BeaconDimmed(int Level);

public record BeaconExtinguished;

/// <summary>
/// A document the shared subscription writes through the session the daemon hands it — one per
/// delivered event, keyed by <see cref="IEvent.Id"/>.
/// </summary>
/// <remarks>
/// Keyed on the event id rather than a fresh one on purpose: it makes the write both idempotent
/// under a rewind and <em>loadable by the suite</em>, which has only <c>LoadAsync</c> to read with —
/// the suites never reach for a query provider. So the suite reads the stream, takes the event ids
/// the store assigned, and loads a note for each.
/// </remarks>
public class ComplianceSubscriptionNote
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
}

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
    private int _commitCount;

    /// <summary>
    /// The event types this subscription declared an allow list for. A registrar's
    /// <c>Subscribe</c> replays these onto whatever the store's own subscription registration uses
    /// to carry filters.
    /// </summary>
    /// <remarks>
    /// A plain list rather than deriving from the shared <c>EventFilterable</c>, because the
    /// products' bare-<c>ISubscription</c> registration paths wrap the subscription in their own
    /// <c>SubscriptionBase</c> and the wrapper — not this object — is what the daemon reads filters
    /// from. Marten's <c>SubscriptionWrapper</c> copies nothing, so a filter declared here only
    /// takes effect if the registrar replays it, which is exactly what
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsSubscriptionEventFilters"/>
    /// gates.
    /// </remarks>
    public List<Type> IncludedEventTypes { get; } = new();

    /// <summary>
    /// Restrict this subscription to an allow list of event types. Must be called before the store
    /// is built, since filters are part of registration.
    /// </summary>
    public void IncludeType<T>() => IncludedEventTypes.Add(typeof(T));

    /// <summary>
    /// Optional read of the <em>committed</em> state, run from the change listener this
    /// subscription returns. Set by a suite before the act.
    /// </summary>
    /// <remarks>
    /// The listener contract is not "it ran" but "it ran <em>after</em> the commit", and only a read
    /// over a separate session can tell those apart. Unlike the outbox's before-commit probe in
    /// <see cref="ProjectionSideEffectCompliance{TFixture,TOperations,TQuerySession}"/>, this one
    /// carries no deadlock hazard: by the time it runs, the batch's transaction is already closed.
    /// </remarks>
    public Func<Task<bool>>? CommitProbe { get; set; }

    /// <summary>
    /// How many times the change listener returned by <c>ProcessEventsAsync</c> was called after a
    /// batch committed. Zero on a store that ignored the return value.
    /// </summary>
    public int CommitCount
    {
        get
        {
            lock (_lock)
            {
                return _commitCount;
            }
        }
    }

    /// <summary>
    /// What <see cref="CommitProbe"/> saw the first time the listener ran. Null when no probe was
    /// set, or when the listener never ran at all.
    /// </summary>
    public bool? VisibleAtCommit { get; private set; }

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
    /// The documents this subscription writes through the session the daemon handed it. Called by
    /// each consumer's partial, which does the <c>operations.Store(note)</c> — the store call itself
    /// cannot be shared, since the session type is the product's own.
    /// </summary>
    /// <remarks>
    /// One per event, so a suite that knows the event ids knows exactly what should be there. This
    /// is what makes "writes through the supplied session are committed with the batch" assertable:
    /// a store that hands out a session it never commits satisfies every delivery fact in this
    /// suite and loses these silently.
    /// </remarks>
    protected static IEnumerable<ComplianceSubscriptionNote> NotesFor(EventRange page)
        => page.Events.Select(x => new ComplianceSubscriptionNote { Id = x.Id, Sequence = x.Sequence });

    /// <summary>
    /// Called by the change listener each consumer's partial returns from its
    /// <c>ProcessEventsAsync</c>, after the batch commits.
    /// </summary>
    /// <remarks>
    /// Public rather than protected because the listener is a separate type implementing the
    /// product's own change listener interface, not a subclass of this one — a consumer is free to
    /// declare it nested or beside the partial, and this member has to be reachable either way.
    /// </remarks>
    public async Task RecordCommitAsync()
    {
        if (CommitProbe != null && VisibleAtCommit == null)
        {
            VisibleAtCommit = await CommitProbe().ConfigureAwait(false);
        }

        lock (_lock)
        {
            _commitCount++;
        }
    }

    /// <summary>
    /// Drop everything recorded so far and clear the probe. Called at the start of every fact,
    /// because one subscription instance serves the whole suite — the configuration delegate is
    /// what the fixture keys a store rebuild on, so a new instance per test would need a new
    /// delegate.
    /// </summary>
    /// <remarks>
    /// Clearing the probe matters more than clearing the counters: a probe left behind by an
    /// earlier fact closes over that fact's fixture, and would run against a disposed store from
    /// inside a later fact's daemon.
    /// </remarks>
    public void Clear()
    {
        CommitProbe = null;
        VisibleAtCommit = null;

        lock (_lock)
        {
            _received.Clear();
            _pageCount = 0;
            _commitCount = 0;
        }
    }

    /// <summary>
    /// Poll until the change listener has run at least once, or give up.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> "wait for non-stale data and then assert the listener fired". The
    /// progression row is written inside the batch's transaction, so non-stale is true the moment
    /// that commits — strictly before a post-commit listener runs. Waiting on it and then asserting
    /// is a race that fails perhaps one full-suite run in several; Fisher's local copy of this test
    /// carries the same note for the same reason. The listener is its own signal.
    /// </remarks>
    public async Task WaitForCommitAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CommitCount > 0) return;
            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for the subscription's change listener to run; " +
            $"the subscription received {Received.Count} event(s) across {PageCount} page(s). " +
            "A store that ignores the listener returned by ProcessEventsAsync fails here.");
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

    /// <summary>
    /// A second instance carrying an allow list, because filters are part of registration and the
    /// suite's other facts must not be filtered.
    /// </summary>
    private static readonly ComplianceSubscription _filteredSubscription = buildFilteredSubscription();

    private static ComplianceSubscription buildFilteredSubscription()
    {
        var subscription = new ComplianceSubscription();
        subscription.IncludeType<BeaconDimmed>();
        return subscription;
    }

    private static void configureCore(ComplianceStoreConfig config)
    {
        config.SchemaName = "compliance_subscriptions";

        config.AddEventType<BeaconLit>();
        config.AddEventType<BeaconDimmed>();
        config.AddEventType<BeaconExtinguished>();
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        configureCore(config);
        config.Subscribe(_subscription);
    };

    private static readonly Action<ComplianceStoreConfig> _filteredConfiguration = config =>
    {
        configureCore(config);
        config.Subscribe(_filteredSubscription);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        _subscription.Clear();
        _filteredSubscription.Clear();
    }

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

    #region Rewind

    /// <summary>
    /// A rewound subscription replays the whole store from the beginning.
    /// </summary>
    /// <remarks>
    /// The recording is cleared before the rewind rather than compared against a doubled total,
    /// because "twice as many events arrived" is also what a redelivery bug looks like — and this
    /// fact is about the rewind, not about deduplication.
    /// </remarks>
    [Fact]
    public async Task a_rewound_subscription_replays_from_scratch()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Amon Din"), new BeaconDimmed(1),
            new BeaconDimmed(2), new BeaconExtinguished());

        var daemon = await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 4, _timeout);

        _subscription.Clear();

        await daemon.RewindSubscriptionAsync(ComplianceSubscription.SubscriptionName,
            CancellationToken.None);

        await _subscription.WaitForStreamEventCountAsync(streamId, 4, _timeout);

        receivedFor(streamId).Count.ShouldBe(4);
    }

    /// <summary>
    /// A rewind to a sequence floor replays only what lies past it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is also how the suite pins a <em>start position floor</em> at all. A store's
    /// configuration-time floor (Marten's <c>SubscribeFromSequence(n)</c> and its siblings) cannot
    /// be asserted portably, because the floor is a literal baked into a static configuration
    /// delegate while the sequence numbers under test are assigned at run time — a suite could only
    /// guess a constant, and would then be testing either "everything arrived" or nothing at all.
    /// <c>RewindSubscriptionAsync</c> takes the same floor and is on the shared
    /// <see cref="Daemon.IProjectionDaemon"/>, so the floor can be chosen <em>after</em> the events
    /// exist, which is what makes the exclusion assertable rather than assumed.
    /// </para>
    /// <para>
    /// The floor is exclusive — a rewind to sequence <c>n</c> replays from <c>n + 1</c> — which is
    /// the same meaning both products' local tests pin.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_rewind_to_a_sequence_floor_replays_only_past_it()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aBeaconAsync(new BeaconLit("Eilenach"), new BeaconDimmed(1),
            new BeaconDimmed(2), new BeaconDimmed(3), new BeaconExtinguished());

        var daemon = await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 5, _timeout);

        var sequences = receivedFor(streamId).Select(x => x.Sequence).OrderBy(x => x).ToArray();
        var floor = sequences[1];
        var expected = sequences.Count(x => x > floor);

        // Chosen so the fact cannot pass by delivering nothing.
        expected.ShouldBe(3);

        _subscription.Clear();

        await daemon.RewindSubscriptionAsync(ComplianceSubscription.SubscriptionName,
            CancellationToken.None, floor);

        await _subscription.WaitForStreamEventCountAsync(streamId, expected, _timeout);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var received = receivedFor(streamId);

        received.Count.ShouldBe(expected);
        received.ShouldAllBe(x => x.Sequence > floor);
    }

    #endregion

    #region The transactional guarantee and the change listener

    /// <summary>
    /// Writes made through the session the daemon supplies are committed with the batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantee that distinguishes a subscription from a webhook: the session is handed over
    /// so its writes land in the batch's transaction alongside the progression row, which is what
    /// makes them exactly-once against the store's own database. A subscription cannot advance past
    /// a range whose writes were rolled back.
    /// </para>
    /// <para>
    /// Deliberately ungated, and the reason is the same one <c>ProjectionCoordinatorCompliance</c>
    /// gives for having no gate: a store that hands out a session it never commits passes every
    /// other fact in this suite. A skippable check would recreate exactly the silence this fact
    /// exists to break. What it costs a consumer is two lines in its
    /// <c>ComplianceSubscription</c> partial — <c>foreach (var note in NotesFor(page))
    /// operations.Store(note);</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task writes_through_the_supplied_session_are_committed_with_the_batch()
    {
        SkipUnlessDaemonIsSupported();

        await ensureNoteStorageAsync();

        var streamId = await aBeaconAsync(new BeaconLit("Nardol"), new BeaconDimmed(4));

        var eventIds = (await eventsForAsync(streamId)).Select(x => x.Id).ToArray();
        eventIds.Length.ShouldBe(2);

        await StartDaemonAsync();
        await _subscription.WaitForStreamEventCountAsync(streamId, 2, _timeout);

        // Polled rather than asserted straight after delivery: the subscription records a page
        // inside ProcessEventsAsync, which runs before the batch it was handed is committed.
        await waitForNotesAsync(eventIds);

        await using var session = OpenSession();

        foreach (var id in eventIds)
        {
            var note = await LoadDocumentAsync<ComplianceSubscriptionNote>(session, id);
            note.ShouldNotBeNull();
        }
    }

    /// <summary>
    /// The change listener a subscription returns is called, and called after the commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failures in one fact, and the weaker half is the one a store is likely to have. A store
    /// that drops the return value of <c>ProcessEventsAsync</c> on the floor never calls the
    /// listener at all and passes every other fact here; that is what
    /// <see cref="ComplianceSubscription.WaitForCommitAsync"/> times out on.
    /// </para>
    /// <para>
    /// The stronger half is the probe. "After the commit" is not decoration: a listener called from
    /// inside the batch delegate fires again on every retry of a transaction that had already
    /// committed, which is the bug Fisher's own copy of this test records. Reading the note over a
    /// separate session is what tells the two apart, and it is safe here — unlike the outbox's
    /// before-commit probe — because the batch's transaction is closed by the time the listener
    /// runs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task the_listener_returned_by_the_subscription_runs_after_the_commit()
    {
        SkipUnlessDaemonIsSupported();

        await ensureNoteStorageAsync();

        var streamId = await aBeaconAsync(new BeaconLit("Erelas"), new BeaconDimmed(5));

        var eventIds = (await eventsForAsync(streamId)).Select(x => x.Id).ToArray();
        var lastId = eventIds[^1];

        _subscription.CommitProbe = async () =>
        {
            await using var probe = OpenSession();
            return await LoadDocumentAsync<ComplianceSubscriptionNote>(probe, lastId) != null;
        };

        await StartDaemonAsync();
        await _subscription.WaitForCommitAsync(_timeout);

        _subscription.CommitCount.ShouldBeGreaterThan(0);

        // What the listener saw is what the batch had already committed, read over a session of
        // its own so it is the database's answer rather than the batch session's.
        _subscription.VisibleAtCommit.ShouldBe(true);
    }

    #endregion

    #region Event type filters

    /// <summary>
    /// A subscription that declared an allow list is handed only those event types.
    /// </summary>
    /// <remarks>
    /// Gated on
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsSubscriptionEventFilters"/>
    /// because the filter has to survive a hop the library cannot make for it. Every product's
    /// bare-<c>ISubscription</c> registration wraps the subscription in its own
    /// <c>SubscriptionBase</c>, and it is the wrapper the daemon reads filters from — Marten's
    /// <c>SubscriptionWrapper</c> copies none of them across. So a registrar has to replay
    /// <see cref="ComplianceSubscription.IncludedEventTypes"/> onto whatever its own registration
    /// call carries filters on, and a store that has not done so skips rather than fails.
    /// </remarks>
    [Fact]
    public async Task event_type_filters_restrict_what_the_subscription_is_handed()
    {
        SkipUnlessDaemonIsSupported();

        Assert.SkipUnless(theFixture.SupportsSubscriptionEventFilters,
            "This event store does not replay a subscription's event type allow list onto its own registration");

        await theFixture.ConfigureAsync(_filteredConfiguration);
        await theFixture.CleanEventDataAsync();
        _filteredSubscription.Clear();

        var streamId = await aBeaconAsync(new BeaconLit("Min-Rimmon"), new BeaconDimmed(1),
            new BeaconExtinguished(), new BeaconDimmed(2));

        await StartDaemonAsync();
        await _filteredSubscription.WaitForStreamEventCountAsync(streamId, 2, _timeout);

        // Settle, so "exactly two" is a real claim rather than a snapshot of a shard mid-page.
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var received = _filteredSubscription.Received.Where(x => x.StreamId == streamId).ToArray();

        received.Length.ShouldBe(2);
        received.ShouldAllBe(x => x.Data is BeaconDimmed);
        received.Select(x => ((BeaconDimmed)x.Data).Level).OrderBy(x => x).ShouldBe(new[] { 1, 2 });
    }

    #endregion

    private async Task<IReadOnlyList<IEvent>> eventsForAsync(Guid streamId)
    {
        await using var session = OpenSession();
        return await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
    }

    /// <summary>
    /// Make sure the store can persist a <see cref="ComplianceSubscriptionNote"/> before the daemon
    /// tries to.
    /// </summary>
    /// <remarks>
    /// Not superstition: a store that creates document storage lazily would otherwise meet this
    /// type for the first time <em>inside</em> a subscription batch, and the resulting failure
    /// would indict the transactional guarantee rather than the schema. The sentinel carries an id
    /// no event will ever have, and every assertion here loads by a known event id, so it is
    /// invisible to the facts.
    /// </remarks>
    private async Task ensureNoteStorageAsync()
    {
        await using var session = OpenSession();
        StoreDocument(session, new ComplianceSubscriptionNote { Id = Guid.NewGuid(), Sequence = -1 });
        await SaveChangesAsync(session);
    }

    private async Task waitForNotesAsync(IReadOnlyList<Guid> eventIds)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        var missing = eventIds.Count;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var session = OpenSession();

            missing = 0;
            foreach (var id in eventIds)
            {
                if (await LoadDocumentAsync<ComplianceSubscriptionNote>(session, id) == null) missing++;
            }

            if (missing == 0) return;

            await Task.Delay(100, Cancellation);
        }

        throw new TimeoutException(
            $"Timed out after {_timeout} waiting for the subscription's writes to commit; " +
            $"{missing} of {eventIds.Count} note(s) never landed. A store that hands the subscription " +
            "a session it does not commit fails here.");
    }
}
