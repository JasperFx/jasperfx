using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Enrichment events and documents

public class EnrichmentTaskAssigned
{
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
}

public class EnrichmentLookupAssigned
{
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
}

public class EnrichmentPing
{
    public Guid TaskId { get; set; }
}

public class EnrichmentTaskSummary
{
    public Guid Id { get; set; }
    public string? AssignedUserName { get; set; }
}

public class EnrichmentLookupSummary
{
    public Guid Id { get; set; }
    public string? AssignedUserName { get; set; }
}

public class EnrichmentUser
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

#endregion

#region Enrichment projections

/// <summary>
/// Enrichment with no external reads — mutates the event data in place before the projection sees
/// it, which is the minimum contract: <c>Project</c> observes the enriched value, not the raw one.
/// </summary>
public partial class SimpleEnrichmentProjection: ComplianceEventProjection
{
    public void Project(EnrichmentTaskAssigned e, ComplianceOperations ops)
    {
        ops.Store(new EnrichmentTaskSummary { Id = e.TaskId, AssignedUserName = e.UserName });
    }

    public override Task EnrichEventsAsync(ComplianceQuerySession querySession,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var e in events.OfType<IEvent<EnrichmentTaskAssigned>>())
        {
            e.Data.UserName = "Enriched User";
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The reason enrichment takes a query session at all: the hook can read persisted documents before
/// the projection runs.
/// </summary>
public partial class DbLookupEnrichmentProjection: ComplianceEventProjection
{
    public void Project(EnrichmentLookupAssigned e, ComplianceOperations ops)
    {
        ops.Store(new EnrichmentLookupSummary { Id = e.TaskId, AssignedUserName = e.UserName });
    }

    public override async Task EnrichEventsAsync(ComplianceQuerySession querySession,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        var assigned = events.OfType<IEvent<EnrichmentLookupAssigned>>().ToArray();
        if (assigned.Length == 0)
        {
            return;
        }

        foreach (var userId in assigned.Select(x => x.Data.UserId).Distinct())
        {
            var user = await querySession.LoadAsync<EnrichmentUser>(userId, cancellation).ConfigureAwait(false);
            if (user == null)
            {
                continue;
            }

            foreach (var e in assigned.Where(x => x.Data.UserId == userId))
            {
                e.Data.UserName = user.Name;
            }
        }
    }
}

/// <summary>
/// Records the order in which the store calls enrichment versus projection.
/// </summary>
/// <remarks>
/// The recording list is static because the suite's store configuration lives in a static delegate
/// (that is what lets the fixture skip redundant rebuilds), so the projection instance is not
/// reachable from a test method. Tests clear it before appending.
/// </remarks>
public partial class EnrichmentCallOrderProjection: ComplianceEventProjection
{
    public static readonly List<string> CallOrder = new();

    public void Project(EnrichmentPing e, ComplianceOperations ops)
    {
        CallOrder.Add($"Apply:{nameof(EnrichmentPing)}");
    }

    public override Task EnrichEventsAsync(ComplianceQuerySession querySession,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        if (events.OfType<IEvent<EnrichmentPing>>().Any())
        {
            CallOrder.Add(nameof(EnrichEventsAsync));
        }

        return Task.CompletedTask;
    }
}

#endregion

/// <summary>
/// <c>IEventEnrichment.EnrichEventsAsync</c> runs before an inline EventProjection applies the same
/// batch, and can read from the store while it does.
/// </summary>
/// <remarks>
/// Each projection owns its own event and document type on purpose: all three are registered against
/// one store, and two projections writing a summary for the same id would just overwrite each other.
/// </remarks>
public abstract class EventProjectionEnrichmentCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_enrichment";

        config.AddProjection(new SimpleEnrichmentProjection(), ProjectionLifecycle.Inline);
        config.AddProjection(new DbLookupEnrichmentProjection(), ProjectionLifecycle.Inline);
        config.AddProjection(new EnrichmentCallOrderProjection(), ProjectionLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public async Task enrichment_sets_data_before_apply_inline()
    {
        await using var session = OpenSession();

        var taskId = Guid.NewGuid();
        EventsFor(session).StartStream(taskId,
            new EnrichmentTaskAssigned { TaskId = taskId, UserId = Guid.NewGuid() });
        await SaveChangesAsync(session);

        var summary = await LoadDocumentAsync<EnrichmentTaskSummary>(session, taskId);

        summary.ShouldNotBeNull();

        // The raw event carries a null UserName. Anything else means enrichment ran first.
        summary.AssignedUserName.ShouldBe("Enriched User");
    }

    [Fact]
    public async Task enrichment_can_read_documents_from_the_store()
    {
        var userId = Guid.NewGuid();

        await using (var seeding = OpenSession())
        {
            StoreDocument(seeding, new EnrichmentUser { Id = userId, Name = "Alice Smith" });
            await SaveChangesAsync(seeding);
        }

        await using var session = OpenSession();

        var taskId = Guid.NewGuid();
        EventsFor(session).StartStream(taskId,
            new EnrichmentLookupAssigned { TaskId = taskId, UserId = userId });
        await SaveChangesAsync(session);

        var summary = await LoadDocumentAsync<EnrichmentLookupSummary>(session, taskId);

        summary.ShouldNotBeNull();
        summary.AssignedUserName.ShouldBe("Alice Smith");
    }

    [Fact]
    public async Task enrichment_is_called_before_apply()
    {
        EnrichmentCallOrderProjection.CallOrder.Clear();

        await using var session = OpenSession();

        var streamId = Guid.NewGuid();
        EventsFor(session).StartStream(streamId, new EnrichmentPing { TaskId = streamId });
        await SaveChangesAsync(session);

        EnrichmentCallOrderProjection.CallOrder
            .ShouldBe(new[] { "EnrichEventsAsync", $"Apply:{nameof(EnrichmentPing)}" });
    }
}
