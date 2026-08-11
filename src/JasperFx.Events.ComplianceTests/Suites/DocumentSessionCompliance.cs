using System;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Session semantics: what <see cref="IDocumentSessionFactory" /> hands back, and where the
/// transaction boundary is.
/// </summary>
/// <remarks>
/// The load-bearing assertion here is <see cref="changes_are_invisible_until_save_changes" />. Every
/// other operation in the contract is meaningless without it — a store that flushed on
/// <c>Store</c> would pass the load, query and delete suites and still be unusable by a consumer
/// that relies on a unit of work.
/// </remarks>
public abstract class DocumentSessionCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_documents";
        config.AddDocumentType<ComplianceWidget>();
        config.AddDocumentType<ComplianceGadget>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    [Fact]
    public async Task lightweight_session_round_trips_a_document()
    {
        var id = Guid.NewGuid();

        await using (var session = LightweightSession())
        {
            session.Store(new ComplianceWidget { Id = id, Name = "Sprocket", Color = "red", Weight = 4 });
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Sprocket");
        loaded.Color.ShouldBe("red");
        loaded.Weight.ShouldBe(4);
    }

    [Fact]
    public async Task changes_are_invisible_until_save_changes()
    {
        var id = Guid.NewGuid();

        await using var session = LightweightSession();
        session.Store(new ComplianceWidget { Id = id, Name = "Uncommitted" });

        await using (var other = QuerySession())
        {
            (await other.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldBeNull();
        }

        await session.SaveChangesAsync(Cancellation);

        await using var after = QuerySession();
        (await after.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task abandoning_a_session_without_saving_discards_the_work()
    {
        var id = Guid.NewGuid();

        await using (var session = LightweightSession())
        {
            session.Store(new ComplianceWidget { Id = id, Name = "Never saved" });
            // deliberately no SaveChangesAsync
        }

        await using var query = QuerySession();
        (await query.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task save_changes_with_no_pending_work_is_a_no_op()
    {
        await using var session = LightweightSession();

        await session.SaveChangesAsync(Cancellation);
        await session.SaveChangesAsync(Cancellation);
    }

    [Fact]
    public async Task saving_twice_does_not_replay_the_first_batch()
    {
        var id = Guid.NewGuid();

        await using var session = LightweightSession();
        session.Store(new ComplianceWidget { Id = id, Name = "First", Weight = 1 });
        await session.SaveChangesAsync(Cancellation);

        session.Store(new ComplianceWidget { Id = id, Name = "Second", Weight = 2 });
        await session.SaveChangesAsync(Cancellation);

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Second");
        loaded.Weight.ShouldBe(2);
    }

    [Fact]
    public async Task sessions_are_independent_units_of_work()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using var one = LightweightSession();
        await using var two = LightweightSession();

        one.Store(new ComplianceWidget { Id = first, Name = "One" });
        two.Store(new ComplianceWidget { Id = second, Name = "Two" });

        await one.SaveChangesAsync(Cancellation);

        await using (var query = QuerySession())
        {
            (await query.LoadAsync<ComplianceWidget>(first, Cancellation)).ShouldNotBeNull();
            (await query.LoadAsync<ComplianceWidget>(second, Cancellation)).ShouldBeNull();
        }

        await two.SaveChangesAsync(Cancellation);

        await using var after = QuerySession();
        (await after.LoadAsync<ComplianceWidget>(second, Cancellation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_query_session_can_read_every_document_type()
    {
        var widgetId = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = widgetId, Name = "Widget" });
        await PersistAsync(new ComplianceGadget { Id = "gadget-1", Kind = "lever" });

        await using var query = QuerySession();

        (await query.LoadAsync<ComplianceWidget>(widgetId, Cancellation)).ShouldNotBeNull();
        (await query.LoadAsync<ComplianceGadget>("gadget-1", Cancellation)).ShouldNotBeNull();
    }
}
