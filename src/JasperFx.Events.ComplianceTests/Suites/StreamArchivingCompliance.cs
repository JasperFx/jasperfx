using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Archiving events

public record LedgerOpened(string Name);

public record LedgerEntryPosted(decimal Amount);

public record LedgerClosed;

/// <summary>
/// Exists so the suite has a snapshot for the store to own. Capturing an <see cref="Archived"/>
/// event only archives the stream when a single-stream projection <em>owns</em> it — the shared
/// <c>JasperFxSingleStreamProjectionBase</c> checks for a snapshot before it archives — so a
/// configuration with no aggregate registered cannot reach the behaviour at all.
/// </summary>
public partial class ComplianceLedger
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static ComplianceLedger Create(LedgerOpened e) => new() { Name = e.Name };

    public void Apply(LedgerEntryPosted e) => Balance += e.Amount;
}

/// <summary>
/// The string-keyed twin of <see cref="ComplianceLedger"/>, for the same reason every other suite
/// here carries one: stream identity is a store-level setting.
/// </summary>
public partial class ComplianceLedgerByKey
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static ComplianceLedgerByKey Create(LedgerOpened e) => new() { Name = e.Name };

    public void Apply(LedgerEntryPosted e) => Balance += e.Amount;
}

/// <summary>
/// The archiving page's own recommended shape: deletion goes through <c>ShouldDelete</c>, and the
/// <see cref="Archived"/> marker rides along in the same save. Carries a <c>ShouldDelete</c> arm so
/// the source generator emits the <c>DetermineAction</c> dispatcher rather than a plain evolver —
/// the path jasperfx#886 was decided per event on.
/// </summary>
public partial class ComplianceClosableLedger
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static ComplianceClosableLedger Create(LedgerOpened e) => new() { Name = e.Name };

    public void Apply(LedgerEntryPosted e) => Balance += e.Amount;

    public bool ShouldDelete(LedgerClosed e) => true;
}

#endregion

/// <summary>
/// <c>ArchiveStream</c> and the consequences of archiving — what a store still reports about an
/// archived stream, and what it stops reporting.
/// </summary>
/// <remarks>
/// <para>
/// Archiving is a good compliance candidate precisely because the call itself is trivial and the
/// <em>consequences</em> are where two implementations drift: whether stream state still answers,
/// whether the events remain readable, whether the archived flag is visible, and whether appending
/// to an archived stream is allowed.
/// </para>
/// <para>
/// Physical partition movement (Marten's archived event partition and Polecat's equivalent) is
/// explicitly out of scope — that is storage layout, and it stays in each product's own tests.
/// </para>
/// </remarks>
public abstract class StreamArchivingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_archiving";
        config.AddEventType<LedgerOpened>();
        config.AddEventType<LedgerEntryPosted>();
        config.AddEventType<LedgerClosed>();
    };

    /// <summary>
    /// Same schema, plus an inline snapshot, so a single-stream projection owns the stream and can
    /// react to an <see cref="Archived"/> event.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _inlineSnapshotConfiguration = config =>
    {
        config.SchemaName = "compliance_archiving_snapshot";
        config.AddEventType<LedgerClosed>();
        config.Snapshot<ComplianceLedger>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _asyncSnapshotConfiguration = config =>
    {
        config.SchemaName = "compliance_archiving_snapshot_async";
        config.AddEventType<LedgerClosed>();
        config.Snapshot<ComplianceLedger>(SnapshotLifecycle.Async);
    };

    /// <summary>
    /// An inline snapshot whose aggregate deletes itself through <c>ShouldDelete</c>, for the
    /// jasperfx#886 batch: a domain "closed" event and the <see cref="Archived"/> marker in one save.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _deletingSnapshotConfiguration = config =>
    {
        config.SchemaName = "compliance_archiving_snapshot_delete";
        config.AddEventType<LedgerClosed>();
        config.Snapshot<ComplianceClosableLedger>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _stringSnapshotConfiguration = config =>
    {
        config.SchemaName = "compliance_archiving_snapshot_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.AddEventType<LedgerClosed>();
        config.Snapshot<ComplianceLedgerByKey>(SnapshotLifecycle.Inline);
    };

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<Guid> aLedgerAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            new LedgerOpened("Petty Cash"),
            new LedgerEntryPosted(25),
            new LedgerEntryPosted(75));
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task archiveAsync(Guid streamId)
    {
        await using var session = OpenSession();
        EventsFor(session).ArchiveStream(streamId);
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task archiving_marks_the_stream_archived_in_its_state()
    {
        var streamId = await aLedgerAsync();

        await using (var reader = OpenSession())
        {
            var before = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);
            before.ShouldNotBeNull();
            before.IsArchived.ShouldBeFalse();
        }

        await archiveAsync(streamId);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();

        // Archiving is not truncation: the version the stream reached is still reported.
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task archiving_is_idempotent()
    {
        var streamId = await aLedgerAsync();

        await archiveAsync(streamId);
        await archiveAsync(streamId);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task archiving_one_stream_leaves_its_neighbours_alone()
    {
        var archived = await aLedgerAsync();
        var untouched = await aLedgerAsync();

        await archiveAsync(archived);

        await using var session = OpenSession();

        var archivedState = await EventsFor(session).FetchStreamStateAsync(archived, Cancellation);
        archivedState.ShouldNotBeNull();
        archivedState.IsArchived.ShouldBeTrue();

        var untouchedState = await EventsFor(session).FetchStreamStateAsync(untouched, Cancellation);
        untouchedState.ShouldNotBeNull();
        untouchedState.IsArchived.ShouldBeFalse();

        var events = await EventsFor(session).FetchStreamAsync(untouched, token: Cancellation);
        events.Count.ShouldBe(3);
    }

    [Fact]
    public async Task events_of_an_archived_stream_report_themselves_as_archived()
    {
        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        // A store is free to exclude archived events from the default stream read or to return
        // them flagged, but it must not return them silently claiming to be live.
        if (events.Any())
        {
            events.ShouldAllBe(x => x.IsArchived);
        }
    }

    [Fact]
    public async Task archiving_a_stream_that_does_not_exist_is_not_an_error()
    {
        var unknown = Guid.NewGuid();

        await archiveAsync(unknown);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(unknown, Cancellation);

        // Nothing was created by archiving a phantom.
        state.ShouldBeNull();
    }

    [Fact]
    public async Task an_archived_stream_can_still_be_aggregated_to_its_last_known_state()
    {
        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        // Whatever the read semantics for archived events, the recorded version survives archiving
        // -- that is what makes archiving reversible bookkeeping rather than deletion.
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
        state.IsArchived.ShouldBeTrue();
    }

    /// <summary>
    /// Archiving is not a soft delete you can keep writing through: the append must not land, and
    /// it must be refused with the store's nominated archived-stream exception.
    /// </summary>
    /// <remarks>
    /// This used to assert only that the commit failed, because the three stores each threw an
    /// unrelated type — Marten its generic <c>InvalidStreamOperationException</c>, Polecat its
    /// <c>InvalidStreamException</c>, Fisher its own <c>ArchivedStreamException</c> — and none was
    /// on the shared surface. jasperfx#871 lifted <see cref="ArchivedStreamException"/>, so the
    /// category is now nameable: a store that adopted it names nothing, and a store that owns its
    /// exception hierarchy points <c>ExceptionTypeFor</c> at its own type.
    /// </remarks>
    [Fact]
    public async Task appending_to_an_archived_stream_is_rejected()
    {
        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);

        await ShouldFailWithAsync(ComplianceExceptionKind.ArchivedStream, async () =>
        {
            await using var session = OpenSession();
            EventsFor(session).Append(streamId, new LedgerEntryPosted(10));
            await SaveChangesAsync(session);
        });

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);

        // Rejected, not partially applied.
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
        state.IsArchived.ShouldBeTrue();
    }

    // ---------- Archiving through an Archived event ----------

    /// <summary>
    /// A stream archives itself when a single-stream projection that owns it processes an
    /// <see cref="Archived"/> event. That routing lives in JasperFx's own
    /// <c>JasperFxSingleStreamProjectionBase</c>, so it is shared code — but every store wires its
    /// own projection storage under it, and Marten carries four local variants of this test for
    /// exactly that reason.
    /// </summary>
    [Fact]
    public async Task capturing_an_archived_event_through_an_inline_snapshot_archives_the_stream()
    {
        await theFixture.ConfigureAsync(_inlineSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceLedger>(streamId,
                new LedgerOpened("Petty Cash"), new LedgerEntryPosted(25));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new LedgerEntryPosted(75), new Archived("Closed out"));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();
        state.Version.ShouldBe(4);
    }

    /// <summary>
    /// jasperfx#886 — the shape the archiving documentation itself recommends: a domain event whose
    /// <c>ShouldDelete</c> arm removes the snapshot, followed by the <see cref="Archived"/> marker, in
    /// one save. Both consequences have to land. Archiving on its own used to be the only one that
    /// did, because the runtime decided the projection's action from the last event rather than from
    /// the batch, so the marker's no-op overwrote the delete and the snapshot survived.
    /// </summary>
    [Fact]
    public async Task a_should_delete_event_followed_by_the_archived_marker_deletes_and_archives()
    {
        await theFixture.ConfigureAsync(_deletingSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceClosableLedger>(streamId,
                new LedgerOpened("Petty Cash"), new LedgerEntryPosted(25));
            await SaveChangesAsync(session);
        }

        // The snapshot has to be there first, or the batch proves nothing: "deleted" and "never
        // stored" look identical afterwards.
        await using (var reader = OpenSession())
        {
            (await LoadDocumentAsync<ComplianceClosableLedger>(reader, streamId)).ShouldNotBeNull();
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new LedgerClosed(), new Archived("Closed out"));
            await SaveChangesAsync(session);
        }

        await using var final = OpenSession();

        (await LoadDocumentAsync<ComplianceClosableLedger>(final, streamId)).ShouldBeNull();

        var state = await EventsFor(final).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();
        state.Version.ShouldBe(4);
    }

    /// <summary>
    /// The inverse, and the reason the fix reads the final snapshot rather than latching the delete:
    /// a document created and deleted inside one batch that started with no snapshot never existed,
    /// so nothing is deleted and the save is not an error.
    /// </summary>
    /// <remarks>
    /// <b>This fact does not discriminate, and is kept as documentation rather than as a guard rail.</b>
    /// Measured against a build with the jasperfx#886 fix reverted, it passes either way: inline, the
    /// phantom action queues a delete for a row that is not there, which is a no-op in SQL, so the
    /// observable end state is identical. Its sibling above does discriminate — it fails with "should be
    /// null but was" on the reverted build.
    /// <para>
    /// Where the phantom delete is actually observable is the async daemon, where
    /// <c>EventRange.MarkSliceAction</c> records a <c>ProjectionDeleted&lt;TDoc,TId&gt;</c> that
    /// downstream stages of a composite projection then receive for a document that never existed.
    /// Pinning that needs a seam this suite does not have yet — see jasperfx#893. Until then the
    /// discriminating coverage for this direction is the unit test
    /// <c>sg_determine_action_reports_nothing_when_created_and_deleted_in_an_initially_empty_batch</c>,
    /// which asserts the <c>ActionType</c> itself against the real source-generated dispatcher.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task creating_and_deleting_within_one_batch_stores_nothing()
    {
        await theFixture.ConfigureAsync(_deletingSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceClosableLedger>(streamId,
                new LedgerOpened("Petty Cash"), new LedgerClosed());
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();

        (await LoadDocumentAsync<ComplianceClosableLedger>(reader, streamId)).ShouldBeNull();

        // The events are persisted either way — only the document write is skipped.
        var events = await EventsFor(reader).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task capturing_an_archived_event_through_an_async_snapshot_archives_the_stream()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceLedger>(streamId,
                new LedgerOpened("Petty Cash"), new LedgerEntryPosted(25));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new Archived("Closed out"));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task capturing_an_archived_event_archives_a_string_identified_stream()
    {
        await theFixture.ConfigureAsync(_stringSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var key = $"ledger/{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceLedgerByKey>(key,
                new LedgerOpened("Petty Cash"), new LedgerEntryPosted(25));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(key, new Archived("Closed out"));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(key, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeTrue();
    }

    /// <summary>
    /// The negative half, and the one that keeps the three facts above from being satisfied by a
    /// store that archives every stream it snapshots: without the <see cref="Archived"/> event, the
    /// same projection over the same events leaves the stream live.
    /// </summary>
    [Fact]
    public async Task a_snapshotted_stream_without_an_archived_event_stays_live()
    {
        await theFixture.ConfigureAsync(_inlineSnapshotConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceLedger>(streamId,
                new LedgerOpened("Petty Cash"), new LedgerEntryPosted(25));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeFalse();
    }

    // ---------- Unarchiving ----------

    private void assertUnarchiveSupported() => Assert.SkipUnless(theFixture.SupportsUnarchiveStream,
        "This event store does not implement UnArchiveStream");

    private async Task unarchiveAsync(Guid streamId)
    {
        await using var session = OpenSession();
        theFixture.UnArchiveStream(session, streamId);
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task unarchiving_clears_is_archived_on_the_stream_state()
    {
        assertUnarchiveSupported();

        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);
        await unarchiveAsync(streamId);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeFalse();
        state.Version.ShouldBe(3);
    }

    /// <summary>
    /// The flag has to come off the <em>events</em>, not just the stream row. A store that flipped
    /// only the stream would pass the state assertion above and still read back an empty stream.
    /// </summary>
    [Fact]
    public async Task unarchiving_restores_the_events_to_an_ordinary_stream_read()
    {
        assertUnarchiveSupported();

        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);
        await unarchiveAsync(streamId);

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(3);
        events.ShouldAllBe(x => !x.IsArchived);
    }

    [Fact]
    public async Task unarchiving_makes_the_stream_appendable_again()
    {
        assertUnarchiveSupported();

        var streamId = await aLedgerAsync();
        await archiveAsync(streamId);
        await unarchiveAsync(streamId);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new LedgerEntryPosted(10));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.Version.ShouldBe(4);
        state.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task unarchiving_a_stream_that_was_never_archived_is_not_an_error()
    {
        assertUnarchiveSupported();

        var streamId = await aLedgerAsync();
        await unarchiveAsync(streamId);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.IsArchived.ShouldBeFalse();
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task unarchiving_one_stream_leaves_its_neighbours_archived()
    {
        assertUnarchiveSupported();

        var restored = await aLedgerAsync();
        var stillArchived = await aLedgerAsync();

        await archiveAsync(restored);
        await archiveAsync(stillArchived);
        await unarchiveAsync(restored);

        await using var session = OpenSession();

        var restoredState = await EventsFor(session).FetchStreamStateAsync(restored, Cancellation);
        restoredState.ShouldNotBeNull();
        restoredState.IsArchived.ShouldBeFalse();

        var archivedState = await EventsFor(session).FetchStreamStateAsync(stillArchived, Cancellation);
        archivedState.ShouldNotBeNull();
        archivedState.IsArchived.ShouldBeTrue();
    }
}
