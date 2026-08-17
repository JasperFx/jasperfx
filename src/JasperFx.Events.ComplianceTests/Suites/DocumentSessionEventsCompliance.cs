using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record KilnFired(string Site);

public record KilnCooled(int DegreesPerHour);

/// <summary>
/// The route from a session the consumer opened to that session's event store — jasperfx#669.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, unlike the other four document suites: it is the one that requires the store to be an
/// event store as well as a document store. A document-only implementer (the in-memory reference
/// store in <c>EventStoreTests</c> is the standing example) correctly leaves
/// <see cref="IDocumentReadOperations.Events" /> on its throwing default and simply does not enroll
/// here.
/// </para>
/// <para>
/// What is under test is the <em>accessor</em>, not the event store behind it — appending, stream
/// state, aggregation and the rest are already held to a definition by the event compliance suites,
/// and re-asserting them here would pin the same behavior twice. So each fact does the least event
/// work that proves the route is real: that it is reachable, that it is the same unit of work as the
/// document session it came from, and that the read tier cannot append.
/// </para>
/// <para>
/// ⚠️ The failure this suite exists to catch is silent. C# interface implementation is not
/// return-type covariant, so a product session that already declares an <c>Events</c> property of
/// its own event-store type does not implement the contract member — it binds to the throwing
/// default instead, with no compile error anywhere. Nothing but a test that actually calls through
/// the contract-typed session will notice.
/// </para>
/// </remarks>
public abstract class DocumentSessionEventsCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
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
    public void a_writable_session_exposes_the_full_event_store_api()
    {
        // Held as the contract, never as the product's own session type — binding the local to the
        // product type is exactly the mistake that makes the non-covariance trap invisible.
        IDocumentSessionOperations session = LightweightSession();

        session.Events.ShouldNotBeNull();
    }

    [Fact]
    public void a_query_session_exposes_the_read_only_event_store_api()
    {
        IDocumentReadOperations session = QuerySession();

        session.Events.ShouldNotBeNull();
    }

    [Fact]
    public async Task appending_through_the_accessor_rides_the_session_unit_of_work()
    {
        var streamKey = Guid.NewGuid().ToString();

        IDocumentSessionOperations session = LightweightSession();
        await using (session)
        {
            session.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("North Ridge"));

            // The load-bearing half: uncommitted, exactly as a document Store() would be. An
            // accessor that wrote straight through would pass every other fact here and still break
            // any consumer that relies on the append and the document write committing together.
            await using (var early = QuerySession())
            {
                var before = await early.Events.FetchStreamStateAsync(streamKey, Cancellation);
                before.ShouldBeNull();
            }

            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var state = await query.Events.FetchStreamStateAsync(streamKey, Cancellation);

        state.ShouldNotBeNull();
        state.Version.ShouldBe(1);
    }

    [Fact]
    public async Task documents_and_events_written_through_one_session_commit_together()
    {
        var streamKey = Guid.NewGuid().ToString();
        var documentId = Guid.NewGuid();

        await using (var session = LightweightSession())
        {
            session.Store(new ComplianceWidget { Id = documentId, Name = "Nacelle" });
            session.Events.StartStream<ComplianceKiln>(streamKey, new KilnFired("South Ridge"));
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();

        (await query.LoadAsync<ComplianceWidget>(documentId, Cancellation)).ShouldNotBeNull();
        (await query.Events.FetchStreamStateAsync(streamKey, Cancellation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task the_accessor_reads_events_back_from_the_stream_it_appended()
    {
        var streamKey = Guid.NewGuid().ToString();

        await using (var session = LightweightSession())
        {
            session.Events.StartStream<ComplianceKiln>(streamKey,
                new KilnFired("East Ridge"), new KilnCooled(120));
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var events = await query.Events.FetchStreamAsync(streamKey, token: Cancellation);

        events.Count.ShouldBe(2);
        events.Select(x => x.Data.GetType())
            .ShouldBe(new[] { typeof(KilnFired), typeof(KilnCooled) });
    }
}

/// <summary>
/// Stream marker for <see cref="DocumentSessionEventsCompliance{TFixture}" />. Never projected —
/// the suite asserts on stream state and raw events, so no store needs a projection registered to
/// run it.
/// </summary>
public class ComplianceKiln
{
    public Guid Id { get; set; }
    public string Site { get; set; } = string.Empty;
    public int DegreesPerHour { get; set; }
}
