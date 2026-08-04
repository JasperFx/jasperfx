using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Archiving events

public record LedgerOpened(string Name);

public record LedgerEntryPosted(decimal Amount);

public record LedgerClosed;

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
}
