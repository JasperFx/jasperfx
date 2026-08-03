using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Document type written by an EventProjection but never registered with the store. See
/// https://github.com/JasperFx/marten/issues/4166.
/// </summary>
public class AuditRecord
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}

public class AuditableEvent
{
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// An EventProjection with an explicit <c>ApplyAsync</c> override that writes a document through
/// <c>operations.Store&lt;T&gt;()</c>. The source generator has to see that call and emit a
/// constructor registering <see cref="AuditRecord"/> as a published type — nothing else in the
/// configuration mentions it.
/// </summary>
/// <remarks>
/// The record id is the stream id rather than a fresh Guid so the end-to-end test can load it back.
/// </remarks>
public partial class AuditRecordProjection: ComplianceEventProjection
{
    public override ValueTask ApplyAsync(ComplianceOperations operations, IEvent e, CancellationToken cancellation)
    {
        switch (e.Data)
        {
            case AuditableEvent:
                // The explicit type argument is load-bearing: the generator's scan of ApplyAsync
                // bodies is syntactic, so it only sees Store<T>/Insert<T>/Update<T> written with one.
                operations.Store<AuditRecord>(new AuditRecord
                {
                    Id = e.StreamId,
                    StreamId = e.StreamId,
                    EventType = e.Data.GetType().Name,
                    Timestamp = e.Timestamp
                });
                break;
        }

        return new ValueTask();
    }
}

/// <summary>
/// The conventional route to the same registration: a <c>Create</c> method whose return type is the
/// published document type.
/// </summary>
public partial class AuditRecordCreatorProjection: ComplianceEventProjection
{
    public AuditRecord Create(AuditableEvent e) => new()
    {
        Id = Guid.NewGuid(), EventType = nameof(AuditableEvent)
    };
}

/// <summary>
/// Document types used by an EventProjection are discovered and registered without the user
/// declaring them — whether they show up as a <c>Store&lt;T&gt;</c> call inside an explicit
/// <c>ApplyAsync</c> override or as the return type of a conventional <c>Create</c> method.
/// </summary>
public abstract class EventProjectionRegistrationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_projection_registration";

        // Only the ApplyAsync projection is registered: the Create-convention one is asserted as a
        // bare object, and registering both would have two projections racing to write an
        // AuditRecord for the same event.
        config.AddProjection(new AuditRecordProjection(), ProjectionLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public void explicit_apply_async_projection_publishes_the_stored_document_type()
    {
        var projection = new AuditRecordProjection();

        projection.PublishedTypes().ShouldContain(typeof(AuditRecord),
            "AuditRecord should be discovered from the operations.Store<AuditRecord>() call in ApplyAsync");
    }

    [Fact]
    public void conventional_create_projection_publishes_its_return_type()
    {
        var projection = new AuditRecordCreatorProjection();

        projection.PublishedTypes().ShouldContain(typeof(AuditRecord),
            "AuditRecord should be discovered from the Create method's return type");
    }

    [Fact]
    public async Task unregistered_document_type_is_persisted_end_to_end()
    {
        // The assertion the registration checks above are a proxy for: if discovery worked, the
        // store has real storage for AuditRecord and the inline projection can write to it.
        await using var session = OpenSession();

        var streamId = Guid.NewGuid();
        EventsFor(session).StartStream(streamId, new AuditableEvent { Description = "created" });
        await SaveChangesAsync(session);

        var record = await LoadDocumentAsync<AuditRecord>(session, streamId);

        record.ShouldNotBeNull();
        record.StreamId.ShouldBe(streamId);
        record.EventType.ShouldBe(nameof(AuditableEvent));
    }
}
