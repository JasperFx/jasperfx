using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentSessionOperations.PendingStreams" /> — the stream actions a session has
/// queued but not yet committed (jasperfx#673).
/// </summary>
/// <remarks>
/// <para>
/// Opt-in for the same reason as <see cref="DocumentSessionEventsCompliance{TFixture}" />, and it
/// reuses that suite's event types and stream marker: there are no pending stream actions without an
/// event store, so a document-only implementer leaves the member on its throwing default and does
/// not enroll here. A store adopting jasperfx#669 but not yet jasperfx#673 enrolls the other suite
/// and not this one.
/// </para>
/// <para>
/// The accessor exists for code that did <em>not</em> do the appending — a listener, or a pre-commit
/// hook deciding something from what the session is about to write. Code at the call site already
/// has the <see cref="StreamAction" />, because <c>StartStream</c> and <c>Append</c> return it. So
/// every fact here reads the collection through a contract-typed session, never through a return
/// value.
/// </para>
/// <para>
/// ⚠️ The first fact is the one that catches the silent failure. The contract's default throws
/// rather than answering empty precisely because empty is indistinguishable from a session with
/// nothing pending — a store that returned empty instead of implementing the member would pass any
/// suite written the other way round and still drop every consumer's work.
/// </para>
/// </remarks>
public abstract class PendingStreamActionsCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_documents";
        config.AddDocumentType<ComplianceWidget>();
        config.AddEventType<KilnFired>();
        config.AddEventType<KilnCooled>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    [Fact]
    public async Task a_session_with_nothing_enlisted_has_no_pending_streams()
    {
        await using var session = LightweightSession();

        // Empty, and specifically not a throw: the contract's default throws, so a store that has
        // not implemented the member fails here rather than anywhere subtler.
        session.PendingStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task enlisting_documents_alone_does_not_produce_a_pending_stream()
    {
        await using var session = LightweightSession();
        session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Flange" });

        session.PendingStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_started_stream_is_pending_before_the_commit()
    {
        var streamKey = Guid.NewGuid().ToString();

        await using var session = LightweightSession();
        session.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("North Ridge"));

        var action = session.PendingStreams.ShouldHaveSingleItem();

        action.Key.ShouldBe(streamKey);
        action.Events.Select(x => x.Data).ShouldBe(new object[] { new KilnFired("North Ridge") });
    }

    [Fact]
    public async Task pending_streams_carry_every_event_enlisted_for_a_stream_in_order()
    {
        var streamKey = Guid.NewGuid().ToString();

        await using var session = LightweightSession();
        session.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("South Ridge"));
        session.Events.Append(streamKey, new KilnCooled(120));

        var action = session.PendingStreams.ShouldHaveSingleItem();

        action.Key.ShouldBe(streamKey);
        action.Events.Select(x => x.Data.GetType())
            .ShouldBe(new[] { typeof(KilnFired), typeof(KilnCooled) });
    }

    [Fact]
    public async Task several_streams_are_all_pending_together()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        await using var session = LightweightSession();
        session.Events.StartStream<ComplianceKiln>(first, new KilnFired("East Ridge"));
        session.Events.StartStream<ComplianceKiln>(second, new KilnFired("West Ridge"));

        session.PendingStreams.Select(x => x.Key).OrderBy(x => x)
            .ShouldBe(new[] { first, second }.OrderBy(x => x));
    }

    [Fact]
    public async Task appending_to_an_existing_stream_is_pending_too()
    {
        var streamKey = Guid.NewGuid().ToString();

        await using (var setup = LightweightSession())
        {
            setup.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("Old Kiln"));
            await setup.SaveChangesAsync(Cancellation);
        }

        await using var session = LightweightSession();
        session.Events.Append(streamKey, new KilnCooled(90));

        var action = session.PendingStreams.ShouldHaveSingleItem();

        action.Key.ShouldBe(streamKey);
        action.Events.Select(x => x.Data).ShouldBe(new object[] { new KilnCooled(90) });
    }

    [Fact]
    public async Task committing_clears_the_pending_streams()
    {
        var streamKey = Guid.NewGuid().ToString();

        await using var session = LightweightSession();
        session.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("Kiln Two"));
        session.PendingStreams.ShouldNotBeEmpty();

        await session.SaveChangesAsync(Cancellation);

        // A session is reusable after a commit, so a collection that still held the committed
        // actions would double-count them on the next read.
        session.PendingStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task pending_streams_are_reported_per_session()
    {
        var mine = Guid.NewGuid().ToString();
        var theirs = Guid.NewGuid().ToString();

        await using var one = LightweightSession();
        await using var two = LightweightSession();

        one.Events.StartStream<ComplianceKiln>(mine, new KilnFired("Mine"));
        two.Events.StartStream<ComplianceKiln>(theirs, new KilnFired("Theirs"));

        one.PendingStreams.ShouldHaveSingleItem().Key.ShouldBe(mine);
        two.PendingStreams.ShouldHaveSingleItem().Key.ShouldBe(theirs);
    }

    /// <remarks>
    /// Split out from the facts above so that a store can gate it alone. Everything else here is
    /// about which actions are present and what they carry; this is the one assertion on
    /// <see cref="StreamActionType" />, which a consumer needs in order to tell a new stream from an
    /// append without going back to the database.
    /// </remarks>
    [Fact]
    public async Task a_pending_stream_reports_whether_it_starts_a_stream_or_appends()
    {
        var existing = Guid.NewGuid().ToString();
        var fresh = Guid.NewGuid().ToString();

        await using (var setup = LightweightSession())
        {
            setup.Events.StartStream<ComplianceKiln>(existing, new KilnFired("Existing"));
            await setup.SaveChangesAsync(Cancellation);
        }

        await using var session = LightweightSession();
        session.Events.StartStream<ComplianceKiln>(fresh, new KilnFired("Fresh"));
        session.Events.Append(existing, new KilnCooled(60));

        var byKey = session.PendingStreams.ToDictionary(x => x.Key!);

        byKey[fresh].ActionType.ShouldBe(StreamActionType.Start);
        byKey[existing].ActionType.ShouldBe(StreamActionType.Append);
    }
}
