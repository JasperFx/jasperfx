using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentWriteOperations.Delete{T}(T)" />, its two identity overloads, and
/// <see cref="IDocumentWriteOperations.DeleteWhere{T}" />.
/// </summary>
/// <remarks>
/// <para>
/// <c>DeleteWhere</c> is the operation most likely to diverge between stores, because it is the only
/// write in the contract whose effect is decided by the database at commit time rather than by the
/// session. Two consequences are asserted here: it must match documents the session never loaded,
/// and it must not fire until <c>SaveChangesAsync</c>.
/// </para>
/// <para>
/// Soft deletes are deliberately not covered. A store may implement deletion however it likes as
/// long as a deleted document stops being visible through <c>LoadAsync</c> and <c>Query</c>; the
/// contract is about visibility, not about rows.
/// </para>
/// </remarks>
public abstract class DocumentDeleteCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
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
    public async Task delete_by_entity()
    {
        var id = Guid.NewGuid();
        var widget = new ComplianceWidget { Id = id, Name = "Doomed" };
        await PersistAsync(widget);

        await using (var session = LightweightSession())
        {
            session.Delete(widget);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        (await query.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task delete_by_guid_identity()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Doomed" });

        await using (var session = LightweightSession())
        {
            session.Delete<ComplianceWidget>(id);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        (await query.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task delete_by_string_identity()
    {
        await PersistAsync(new ComplianceGadget { Id = "gadget-doomed", Kind = "lever" });

        await using (var session = LightweightSession())
        {
            session.Delete<ComplianceGadget>("gadget-doomed");
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        (await query.LoadAsync<ComplianceGadget>("gadget-doomed", Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task deleting_a_missing_identity_is_a_no_op()
    {
        await using var session = LightweightSession();

        session.Delete<ComplianceWidget>(Guid.NewGuid());
        session.Delete<ComplianceGadget>("never-existed");

        await session.SaveChangesAsync(Cancellation);
    }

    [Fact]
    public async Task delete_is_not_applied_until_save_changes()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Still here" });

        await using var session = LightweightSession();
        session.Delete<ComplianceWidget>(id);

        await using (var before = QuerySession())
        {
            (await before.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldNotBeNull();
        }

        await session.SaveChangesAsync(Cancellation);

        await using var after = QuerySession();
        (await after.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task delete_where_removes_only_the_matching_documents()
    {
        await PersistAsync(
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "keep", Color = "blue" },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "drop", Color = "red" },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "drop", Color = "red" });

        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceWidget>(x => x.Color == "red");
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var survivors = await query.Query<ComplianceWidget>().ToListAsync(Cancellation);

        survivors.Count.ShouldBe(1);
        survivors.Single().Color.ShouldBe("blue");
    }

    [Fact]
    public async Task delete_where_matches_documents_the_session_never_loaded()
    {
        await PersistAsync(
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "heavy", Weight = 100 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "light", Weight = 1 });

        // A brand new session that has never seen either document.
        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceWidget>(x => x.Weight > 50);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var survivors = await query.Query<ComplianceWidget>().ToListAsync(Cancellation);

        survivors.Count.ShouldBe(1);
        survivors.Single().Name.ShouldBe("light");
    }

    [Fact]
    public async Task delete_where_is_not_applied_until_save_changes()
    {
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "doomed", Color = "red" });

        await using var session = LightweightSession();
        session.DeleteWhere<ComplianceWidget>(x => x.Color == "red");

        await using (var before = QuerySession())
        {
            (await before.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
        }

        await session.SaveChangesAsync(Cancellation);

        await using var after = QuerySession();
        (await after.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(0);
    }

    [Fact]
    public async Task delete_where_matching_nothing_is_a_no_op()
    {
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "safe", Color = "green" });

        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceWidget>(x => x.Color == "chartreuse");
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        (await query.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task delete_only_touches_the_named_document_type()
    {
        var widgetId = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = widgetId, Name = "widget", Weight = 100 });
        await PersistAsync(new ComplianceGadget { Id = "gadget-1", Kind = "lever", Weight = 100 });

        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceWidget>(x => x.Weight == 100);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        (await query.LoadAsync<ComplianceWidget>(widgetId, Cancellation)).ShouldBeNull();
        (await query.LoadAsync<ComplianceGadget>("gadget-1", Cancellation)).ShouldNotBeNull();
    }
}
