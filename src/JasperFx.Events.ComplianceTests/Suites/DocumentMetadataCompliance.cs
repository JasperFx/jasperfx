using System;
using System.Threading.Tasks;
using JasperFx.Core.Reflection;
using JasperFx.Documents;
using JasperFx.Metadata;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The document metadata a store stamps without being asked, read back through the shared marker
/// interfaces and the diagnostics surface (jasperfx#785 §5.2).
/// </summary>
/// <remarks>
/// <para>
/// No contract member is added. <see cref="ITracked" /> is already shared, a session's correlation id,
/// causation id and user name are already <see cref="IMetadataContext" />, and created / last-modified
/// already come back on <see cref="StoredDocument" />. Each group of facts is gated on what it needs:
/// <see cref="DocumentStorageComplianceFixture.SupportsTrackedDocuments" /> for the tracking facts and
/// <see cref="DocumentStorageComplianceFixture.SupportsDocumentDiagnostics" /> for the timestamps.
/// </para>
/// <para>
/// <see cref="IVersioned" /> is not repeated here; <c>GuidOptimisticConcurrencyCompliance</c> owns it.
/// Deliberately excluded, per the survey: a <c>MetadataForAsync</c> accessor (no consumer has asked for
/// one), opt-in column configuration (DDL), the <c>CreatedSince</c> / <c>ModifiedSince</c> operators (the
/// marker-method problem again) and <c>dotnet_type</c> (Fisher cannot map it).
/// </para>
/// </remarks>
public abstract class DocumentMetadataCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_document_metadata";
        config.AddDocumentType<ComplianceAuditedNote>();
        config.AddDocumentType<ComplianceWidget>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync().ConfigureAwait(false);
        await theFixture.CleanDocumentDataAsync().ConfigureAwait(false);
    }

    private void SkipUnlessTracked()
        => Assert.SkipUnless(theFixture.SupportsTrackedDocuments,
            "This store does not stamp ITracked documents from the session (jasperfx#785).");

    private IDocumentStoreDiagnostics Diagnostics
    {
        get
        {
            Assert.SkipUnless(theFixture.SupportsDocumentDiagnostics,
                "This store does not implement IDocumentStoreDiagnostics, which is how the timestamps are read.");
            return theFixture.DocumentDiagnostics;
        }
    }

    private static readonly string WidgetType = typeof(ComplianceWidget).FullNameInCode();

    private async Task SaveTrackedAsync(ComplianceAuditedNote note, string correlationId, string causationId,
        string userName)
    {
        await using var session = LightweightSession();
        theFixture.SetSessionMetadata(session, correlationId, causationId, userName);
        session.Store(note);
        await session.SaveChangesAsync(Cancellation);
    }

    private async Task<ComplianceAuditedNote> LoadNoteAsync(Guid id)
    {
        await using var query = QuerySession();
        return (await query.LoadAsync<ComplianceAuditedNote>(id, Cancellation)).ShouldNotBeNull();
    }

    // ------------------------------------------------------------ ITracked

    [Fact]
    public async Task a_tracked_document_carries_the_sessions_metadata()
    {
        SkipUnlessTracked();
        var note = new ComplianceAuditedNote { Id = Guid.NewGuid(), Text = "first" };

        await SaveTrackedAsync(note, "correlation-1", "causation-1", "alice");

        var loaded = await LoadNoteAsync(note.Id);
        loaded.CorrelationId.ShouldBe("correlation-1");
        loaded.CausationId.ShouldBe("causation-1");
        loaded.LastModifiedBy.ShouldBe("alice");
    }

    [Fact]
    public async Task the_session_wins_over_values_already_on_the_document()
    {
        SkipUnlessTracked();
        var note = new ComplianceAuditedNote
        {
            Id = Guid.NewGuid(), Text = "stale", CorrelationId = "on-the-document", CausationId = "on-the-document",
            LastModifiedBy = "on-the-document"
        };

        await SaveTrackedAsync(note, "correlation-2", "causation-2", "bob");

        var loaded = await LoadNoteAsync(note.Id);
        loaded.CorrelationId.ShouldBe("correlation-2");
        loaded.CausationId.ShouldBe("causation-2");
        loaded.LastModifiedBy.ShouldBe("bob");
    }

    [Fact]
    public async Task each_save_restamps_the_tracking_metadata()
    {
        SkipUnlessTracked();
        var note = new ComplianceAuditedNote { Id = Guid.NewGuid(), Text = "first" };
        await SaveTrackedAsync(note, "correlation-a", "causation-a", "alice");

        var edited = await LoadNoteAsync(note.Id);
        edited.Text = "second";
        await SaveTrackedAsync(edited, "correlation-b", "causation-b", "bob");

        var loaded = await LoadNoteAsync(note.Id);
        loaded.Text.ShouldBe("second");
        loaded.CorrelationId.ShouldBe("correlation-b");
        loaded.CausationId.ShouldBe("causation-b");
        loaded.LastModifiedBy.ShouldBe("bob");
    }

    // ------------------------------------------------------------ timestamps

    [Fact]
    public async Task created_does_not_move_on_an_update()
    {
        var diagnostics = Diagnostics;
        var widget = new ComplianceWidget { Id = Guid.NewGuid(), Name = "original" };
        await PersistAsync(widget);
        var before = (await diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;
        Assert.SkipWhen(before.Created is null, "This store does not track a created timestamp for the type.");

        await Task.Delay(50, Cancellation);
        widget.Name = "edited";
        await PersistAsync(widget);

        var after = (await diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;
        after.Created.ShouldBe(before.Created);
    }

    [Fact]
    public async Task last_modified_moves_forward_on_an_update()
    {
        var diagnostics = Diagnostics;
        var widget = new ComplianceWidget { Id = Guid.NewGuid(), Name = "original" };
        await PersistAsync(widget);
        var before = (await diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;

        await Task.Delay(50, Cancellation);
        widget.Name = "edited";
        await PersistAsync(widget);

        var after = (await diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;
        after.LastModified.ShouldNotBeNull();
        after.LastModified!.Value.ShouldBeGreaterThan(before.LastModified!.Value);
    }
}
