using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Side effect events, aggregate and projection

public record WatchtowerManned(string Name);

public record WatchtowerRelieved(string Watcher);

/// <summary>
/// Raised by the projection back onto the stream it just folded.
/// </summary>
public record WatchtowerAudited(string Name);

/// <summary>
/// Published by the projection through the store's message outbox.
/// </summary>
public record WatchtowerReported(string Name);

public partial class ComplianceWatchtower
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Reliefs { get; set; }
    public bool Audited { get; set; }
}

/// <summary>
/// A single stream projection that both appends an event and publishes a message from
/// <c>RaiseSideEffects</c> — the two things that hook can do.
/// </summary>
/// <remarks>
/// <para>
/// The base type comes from the consumer-supplied <c>ComplianceWatchtowerProjectionBase</c> global
/// alias, and the first parameter of <c>RaiseSideEffects</c> from the existing
/// <c>ComplianceOperations</c> alias, because each product closes
/// <c>JasperFxSingleStreamProjectionBase</c> over its own session pair and this file cannot reach
/// the suite's generics.
/// </para>
/// <para>
/// The guard is load-bearing rather than tidiness. A raised event lands in the event store like any
/// other, takes the next sequence, and is therefore read back by the shard on its next pass — so an
/// unguarded raise is an append loop that never quiesces and the daemon never reports non-stale.
/// Both products' local tests carry the same guard for the same reason.
/// </para>
/// </remarks>
public partial class ComplianceWatchtowerProjection: ComplianceWatchtowerProjectionBase
{
    public static ComplianceWatchtower Create(IEvent<WatchtowerManned> @event) =>
        new() { Id = @event.StreamId, Name = @event.Data.Name };

    public void Apply(WatchtowerRelieved _, ComplianceWatchtower tower) => tower.Reliefs++;

    public void Apply(WatchtowerAudited _, ComplianceWatchtower tower) => tower.Audited = true;

    public override ValueTask RaiseSideEffects(ComplianceOperations operations,
        IEventSlice<ComplianceWatchtower> slice)
    {
        if (slice.Snapshot == null)
        {
            return new ValueTask();
        }

        if (slice.Events().Any(x => x.Data is WatchtowerAudited))
        {
            return new ValueTask();
        }

        slice.AppendEvent(new WatchtowerAudited(slice.Snapshot.Name));
        slice.PublishMessage(new WatchtowerReported(slice.Snapshot.Name));

        return new ValueTask();
    }
}

#endregion

/// <summary>
/// <c>RaiseSideEffects</c> — the projection hook that appends events of its own and publishes
/// messages, and the store-side plumbing that has to carry both.
/// </summary>
/// <remarks>
/// <para>
/// The hook itself is shared: it is declared on <c>JasperFxAggregationProjectionBase</c>, and the
/// shared single- and multi-stream bases are what drain <c>slice.PublishedMessages()</c> into the
/// projection batch and <c>slice.RaisedEvents()</c> into append operations. What is <em>not</em>
/// shared is the half each store has to supply underneath — building those append operations, and
/// handing out a message batch — and that half is exactly what has been shipped stubbed empty
/// twice (fisher#61, polecat#420), in both cases dropping every raised event with no error and no
/// log. A projection whose side effects silently go nowhere passes every other suite in this
/// library.
/// </para>
/// <para>
/// Hence the shape of the assertions. <see cref="RecordingMessageOutbox"/> is supplied by the suite
/// and every message fact asserts a <strong>nonzero</strong> publish count, for the same reason
/// <c>AggregateWriteCacheCompliance</c> asserts a nonzero hit count: "the projection produced the
/// right document" and "nothing threw" are both vacuously true of a store that discarded the side
/// effects. The rebuild fact reads the publish count from before the rebuild and requires it to be
/// positive <em>first</em>, so "no messages were published during the rebuild" cannot pass by
/// having published nothing at all.
/// </para>
/// <para>
/// Two configurations rather than one, and deliberately: the outbox facts install the outbox
/// through <see cref="IComplianceStoreRegistrar.UseMessageOutbox"/>, which carries a throwing
/// default, so a store that has not implemented it would fail at store <em>construction</em> and
/// take the raised-event facts down with it. Keeping the outbox out of the suite's standard
/// configuration means the gate can be checked before the member is ever reached.
/// </para>
/// </remarks>
public abstract class ProjectionSideEffectCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One outbox instance for the whole suite, because the configuration delegate is what the
    /// fixture keys a store rebuild on — a new instance per test would need a new delegate. Every
    /// fact calls <see cref="RecordingMessageOutbox.Reset"/> before acting.
    /// </summary>
    private static readonly RecordingMessageOutbox _outbox = new();

    private static void configureCore(ComplianceStoreConfig config)
    {
        config.SchemaName = "compliance_side_effects";

        config.AddEventType<WatchtowerManned>();
        config.AddEventType<WatchtowerRelieved>();
        config.AddEventType<WatchtowerAudited>();

        // Async with the daemon started only by the facts that want it, so the facts about a plain
        // session's unit of work cannot be disturbed by a projection running inline.
        config.AddProjection(new ComplianceWatchtowerProjection(), ProjectionLifecycle.Async);
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = configureCore;

    private static readonly Action<ComplianceStoreConfig> _outboxConfiguration = config =>
    {
        configureCore(config);
        config.UseMessageOutbox(_outbox);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    /// <summary>
    /// Rebuild the store with the outbox installed, and start from a clean slate. Skips first, so a
    /// store without an outbox never reaches the registrar's throwing default.
    /// </summary>
    private async Task WithOutboxAsync()
    {
        Assert.SkipUnless(theFixture.SupportsMessageOutbox,
            "This event store has no message outbox for projection side effects");

        await theFixture.ConfigureAsync(_outboxConfiguration);
        await theFixture.CleanEventDataAsync();

        _outbox.Reset();
    }

    private async Task<Guid> aWatchtowerAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            events.Length == 0 ? [new WatchtowerManned("Amon Din")] : events);
        await SaveChangesAsync(session);

        return streamId;
    }

    /// <summary>
    /// Poll until the stream reaches an expected version, which is the only signal that means what
    /// these tests need.
    /// </summary>
    /// <remarks>
    /// Not <c>WaitForNonStaleProjectionDataAsync</c>: a raised event takes a sequence of its own, so
    /// the shard goes stale again the instant it writes one. Non-stale can therefore be true after
    /// the original events were folded and before the raised event exists — which is precisely the
    /// window every fact here would read in.
    /// </remarks>
    private async Task WaitForStreamVersionAsync(Guid streamId, long version)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        long actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var session = OpenSession();
            var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
            actual = state?.Version ?? 0;

            if (actual >= version) return;

            await Task.Delay(100, Cancellation);
        }

        throw new TimeoutException(
            $"Timed out after {_timeout} waiting for stream {streamId} to reach version {version}; it is at {actual}.");
    }

    private async Task<IReadOnlyList<IEvent>> eventsForAsync(Guid streamId)
    {
        await using var session = OpenSession();
        return await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
    }

    [Fact]
    public async Task a_raised_event_is_appended_to_its_stream()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Amon Din"));

        await StartDaemonAsync();
        await WaitForStreamVersionAsync(streamId, 2);

        var events = await eventsForAsync(streamId);

        // The original, plus the one the projection raised.
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<WatchtowerAudited>().Name.ShouldBe("Amon Din");
    }

    /// <summary>
    /// The raised event is numbered from the stream's real version rather than from the slice's own
    /// event count.
    /// </summary>
    /// <remarks>
    /// The two answers agree only when the projection has seen every event on the stream, which is
    /// why this fact appends three rather than one: a store numbering the raised event from the
    /// slice would write version 2 over an event that already exists, or leave a hole.
    /// </remarks>
    [Fact]
    public async Task a_raised_event_continues_the_streams_version_sequence()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Eilenach"),
            new WatchtowerRelieved("Beregond"), new WatchtowerRelieved("Bergil"));

        await StartDaemonAsync();
        await WaitForStreamVersionAsync(streamId, 4);

        var events = await eventsForAsync(streamId);

        events.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3, 4 });
        events[3].Data.ShouldBeOfType<WatchtowerAudited>();

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(4);
    }

    /// <summary>
    /// A rebuild replays the stream with side effects suppressed.
    /// </summary>
    /// <remarks>
    /// The failure this catches is not cosmetic. A rebuild that raised side effects again would
    /// append the audit event a second time on every rebuild, so the stream grows without bound and
    /// each growth feeds the shard more events to replay. Asserting on the stream rather than on the
    /// aggregate is what makes it visible: the projected document is idempotent under a re-raise and
    /// looks identical either way.
    /// </remarks>
    [Fact]
    public async Task side_effects_are_suppressed_during_a_rebuild()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Nardol"),
            new WatchtowerRelieved("Beregond"));

        var daemon = await StartDaemonAsync();
        await WaitForStreamVersionAsync(streamId, 3);

        // Anti-vacuous: the raise has to have happened at all before "it did not happen again"
        // means anything.
        var before = await eventsForAsync(streamId);
        before.Count(x => x.Data is WatchtowerAudited).ShouldBe(1);

        await daemon.StopAllAsync();

        // nameof(ComplianceWatchtower), the DOCUMENT type, not the projection class:
        // JasperFxAggregationProjectionBase's constructor sets Name = typeof(TDoc).NameInCode(),
        // overriding ProjectionBase's class-name default, so an aggregation projection is known to
        // the daemon by the type it produces. Naming the class here threw
        // ArgumentOutOfRangeException on every store (found enrolling this suite in polecat#556).
        await daemon.RebuildProjectionAsync(nameof(ComplianceWatchtower), _timeout,
            CancellationToken.None);

        var after = await eventsForAsync(streamId);

        after.Count.ShouldBe(before.Count);
        after.Count(x => x.Data is WatchtowerAudited).ShouldBe(1);

        // And the rebuild still produced the aggregate it was supposed to.
        await using var session = OpenSession();
        var tower = await LoadDocumentAsync<ComplianceWatchtower>(session, streamId);
        tower.ShouldNotBeNull();
        tower.Name.ShouldBe("Nardol");
        tower.Reliefs.ShouldBe(1);
    }

    [Fact]
    public async Task a_message_published_from_a_projection_reaches_the_outbox()
    {
        SkipUnlessDaemonIsSupported();
        await WithOutboxAsync();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Erelas"));

        await StartDaemonAsync();
        await WaitForStreamVersionAsync(streamId, 2);

        await waitForPublishedAsync<WatchtowerReported>();

        var published = _outbox.PublishedMessages.OfType<WatchtowerReported>().ToArray();

        published.Length.ShouldBeGreaterThan(0);
        published.ShouldContain(x => x.Name == "Erelas");
    }

    /// <summary>
    /// The hooks bracket the commit, in order.
    /// </summary>
    /// <remarks>
    /// That ordering is the whole contract: an outbox persisting rows in the before hook gets
    /// atomicity with the write, and one publishing to a broker in the after hook gets the guarantee
    /// that the write actually landed. Driven from a plain session rather than the daemon because
    /// the session's own unit of work is the shorter path to the same boundary and needs no
    /// polling — <c>SaveChangesAsync</c> does not return until both hooks have run.
    /// </remarks>
    [Fact]
    public async Task both_commit_hooks_fire_in_order()
    {
        await WithOutboxAsync();

        await using var session = OpenSession();
        var sink = await session.GetOrStartMessageSink();
        await sink.PublishAsync(new WatchtowerReported("Min-Rimmon"), StorageConstants.DefaultTenantId);

        EventsFor(session).StartStream(Guid.NewGuid(), new WatchtowerManned("Min-Rimmon"));
        await SaveChangesAsync(session);

        _outbox.BatchCount.ShouldBe(1);
        _outbox.Batches[0].Published.Count.ShouldBe(1);
        _outbox.Batches[0].Hooks.ShouldBe(new[] { "before", "after" });
    }

    /// <summary>
    /// The before hook really is inside the transaction and the after hook really is outside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hook order alone does not prove this — the two would fire in that order even if both ran
    /// before the commit, or both after. What separates them is what the rest of the database can
    /// see at the moment each runs, so the probe reads the committed events over a session of its
    /// own: invisible in the before hook, visible in the after hook.
    /// </para>
    /// <para>
    /// Gated on <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsCommitVisibilityProbe"/>
    /// because the before-commit read is the one place in this library where a suite deliberately
    /// reads a row an open transaction is writing. A snapshot reader answers immediately; a
    /// lock-based one blocks, and a probe that blocks until the commit deadlocks against the hook
    /// holding the commit open.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task the_before_hook_runs_inside_the_transaction_and_the_after_hook_outside_it()
    {
        await WithOutboxAsync();

        Assert.SkipUnless(theFixture.SupportsCommitVisibilityProbe,
            "This event store cannot read committed state while its own write transaction is open");

        var streamId = Guid.NewGuid();

        _outbox.Probe = async () =>
        {
            await using var probe = OpenSession();
            var state = await EventsFor(probe).FetchStreamStateAsync(streamId, Cancellation);
            return state != null;
        };

        await using var session = OpenSession();
        var sink = await session.GetOrStartMessageSink();
        await sink.PublishAsync(new WatchtowerReported("Calenhad"), StorageConstants.DefaultTenantId);

        EventsFor(session).StartStream(streamId, new WatchtowerManned("Calenhad"));
        await SaveChangesAsync(session);

        _outbox.Batches[0].VisibleAtBeforeCommit.ShouldBe(false);
        _outbox.Batches[0].VisibleAtAfterCommit.ShouldBe(true);
    }

    /// <summary>
    /// A unit of work that fails publishes nothing.
    /// </summary>
    /// <remarks>
    /// A stream id collision is caught by the append itself, well before either hook — so neither
    /// runs, and the messages the session buffered go nowhere. Asserted as "no hook fired" rather
    /// than "the after hook did not fire", because the weaker claim would pass even if the failure
    /// had never reached the hook boundary at all.
    /// </remarks>
    [Fact]
    public async Task a_unit_of_work_that_fails_publishes_nothing()
    {
        await WithOutboxAsync();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Halifirien"));

        _outbox.Reset();

        await using var session = OpenSession();
        var sink = await session.GetOrStartMessageSink();
        await sink.PublishAsync(new WatchtowerReported("never sent"), StorageConstants.DefaultTenantId);

        // Same id, so the append itself fails.
        EventsFor(session).StartStream(streamId, new WatchtowerManned("Halifirien again"));

        await ShouldFailWithAsync<ExistingStreamIdCollisionException>(() => SaveChangesAsync(session));

        _outbox.CommittedBatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_session_that_never_publishes_never_asks_the_outbox_for_a_batch()
    {
        await WithOutboxAsync();

        await using var session = OpenSession();
        EventsFor(session).StartStream(Guid.NewGuid(), new WatchtowerManned("Amon Anwar"));
        await SaveChangesAsync(session);

        _outbox.BatchCount.ShouldBe(0);
    }

    /// <summary>
    /// A rebuild publishes nothing, and the count it is compared against is required to be positive
    /// first.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="side_effects_are_suppressed_during_a_rebuild"/> on the message
    /// side, and the one that most needs the anti-vacuous guard: "no messages were published during
    /// the rebuild" is trivially satisfied by a store that never published any message at all.
    /// </remarks>
    [Fact]
    public async Task no_messages_are_published_during_a_rebuild()
    {
        SkipUnlessDaemonIsSupported();
        await WithOutboxAsync();

        var streamId = await aWatchtowerAsync(new WatchtowerManned("Halifirien"),
            new WatchtowerRelieved("Beregond"));

        var daemon = await StartDaemonAsync();
        await WaitForStreamVersionAsync(streamId, 3);
        await waitForPublishedAsync<WatchtowerReported>();

        var before = _outbox.PublishedMessages.OfType<WatchtowerReported>().Count();
        before.ShouldBeGreaterThan(0);

        await daemon.StopAllAsync();

        // nameof(ComplianceWatchtower), the DOCUMENT type, not the projection class:
        // JasperFxAggregationProjectionBase's constructor sets Name = typeof(TDoc).NameInCode(),
        // overriding ProjectionBase's class-name default, so an aggregation projection is known to
        // the daemon by the type it produces. Naming the class here threw
        // ArgumentOutOfRangeException on every store (found enrolling this suite in polecat#556).
        await daemon.RebuildProjectionAsync(nameof(ComplianceWatchtower), _timeout,
            CancellationToken.None);

        _outbox.PublishedMessages.OfType<WatchtowerReported>().Count().ShouldBe(before);
    }

    /// <summary>
    /// Poll until at least one message of the given type has reached the outbox.
    /// </summary>
    /// <remarks>
    /// The daemon fires its commit hooks after the batch commits, so the stream version this suite
    /// otherwise waits on can already be right while the publish is still in flight.
    /// </remarks>
    private async Task waitForPublishedAsync<T>()
    {
        var deadline = DateTimeOffset.UtcNow.Add(_timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_outbox.PublishedMessages.OfType<T>().Any()) return;
            await Task.Delay(100, Cancellation);
        }

        throw new TimeoutException(
            $"Timed out after {_timeout} waiting for a {typeof(T).Name} to reach the outbox; " +
            $"it received {_outbox.PublishedMessages.Count} message(s) across {_outbox.BatchCount} batch(es).");
    }
}
