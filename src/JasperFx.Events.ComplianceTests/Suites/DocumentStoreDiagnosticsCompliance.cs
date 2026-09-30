using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using JasperFx.Core.Reflection;
using JasperFx.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentStoreDiagnostics" /> and its write-side sibling
/// <see cref="IDocumentStoreDiagnosticsWriter" /> — the surface a monitoring console browses and edits
/// documents through, by type name and raw JSON (jasperfx#870).
/// </summary>
/// <remarks>
/// <para>
/// jasperfx#870 read the three implementations side by side and found them disagreeing on soft-deleted
/// rows, hierarchies, a missing tenant, and how an id is matched. It made each of those part of the
/// contract; this suite is where they are pinned, and it is the "store-parity test first" the issue asks
/// for before per-store fixes.
/// </para>
/// <para>
/// <b>Setup goes through the session contract, assertions through the diagnostics one.</b> That is
/// the point rather than a convenience: a console reads documents an application wrote, so what is under
/// test is whether the diagnostics view of the store agrees with the application's. The write facts run
/// the other way round and read back through a session, for the same reason.
/// </para>
/// <para>
/// Four gates, each on the fixture: <see cref="DocumentStorageComplianceFixture.SupportsDocumentDiagnostics" />
/// for the whole suite, <see cref="DocumentStorageComplianceFixture.SupportsDocumentDiagnosticWrites" />
/// for the write facts, and the two config-replay flags for the soft-delete and hierarchy facts. The
/// criteria facts are not gated but <em>forked</em> on
/// <see cref="DocumentStorageComplianceFixture.SupportsDocumentDiagnosticCriteria" />: a store without
/// predicate support is still held to refusing one. The tenancy facts reuse
/// <see cref="DocumentStorageComplianceFixture.SupportsConjoinedDocuments" />.
/// </para>
/// </remarks>
public abstract class DocumentStoreDiagnosticsCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly string WidgetType = typeof(ComplianceWidget).FullNameInCode();
    private static readonly string GadgetType = typeof(ComplianceGadget).FullNameInCode();
    private static readonly string TicketType = typeof(ComplianceTicket).FullNameInCode();
    private static readonly string VehicleType = typeof(ComplianceVehicle).FullNameInCode();
    private static readonly string TruckType = typeof(ComplianceTruck).FullNameInCode();
    private static readonly string BusType = typeof(ComplianceBus).FullNameInCode();

    private static void ConfigureCommon(DocumentComplianceConfig config)
    {
        config.AddDocumentType<ComplianceWidget>();
        config.AddDocumentType<ComplianceGadget>();
        config.AddDocumentType<ComplianceTicket>();
        config.AddDocumentType<ComplianceVehicle>();

        config.SoftDeleted<ComplianceTicket>();
        config.AddSubClass<ComplianceVehicle, ComplianceTruck>();
        config.AddSubClass<ComplianceVehicle, ComplianceBus>();
    }

    private static readonly Action<DocumentComplianceConfig> _singleTenanted = config =>
    {
        config.SchemaName = "compliance_doc_diagnostics";
        ConfigureCommon(config);
    };

    // A separate delegate rather than a conditional inside one: ConfigureAsync keys on delegate
    // identity, and a store without conjoined document tenancy may fail to BUILD with the declaration.
    private static readonly Action<DocumentComplianceConfig> _withConjoinedGadgets = config =>
    {
        config.SchemaName = "compliance_doc_diagnostics";
        ConfigureCommon(config);
        config.Conjoined<ComplianceGadget>();
    };

    protected override Action<DocumentComplianceConfig> Configuration
        => theFixture.SupportsConjoinedDocuments ? _withConjoinedGadgets : _singleTenanted;

    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);

        if (!theFixture.SupportsDocumentDiagnostics)
        {
            return;
        }

        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanDocumentDataAsync().ConfigureAwait(false);
    }

    private IDocumentStoreDiagnostics Diagnostics
    {
        get
        {
            Assert.SkipUnless(theFixture.SupportsDocumentDiagnostics,
                "This store does not implement IDocumentStoreDiagnostics (jasperfx#870).");
            return theFixture.DocumentDiagnostics;
        }
    }

    private IDocumentStoreDiagnosticsWriter Writer
    {
        get
        {
            _ = Diagnostics;
            Assert.SkipUnless(theFixture.SupportsDocumentDiagnosticWrites,
                "This store does not implement IDocumentStoreDiagnosticsWriter (jasperfx#870 §6).");
            return theFixture.DocumentDiagnosticsWriter;
        }
    }

    private void SkipUnlessSoftDeletes()
        => Assert.SkipUnless(theFixture.SupportsSoftDeletedDocuments,
            "This fixture does not replay DocumentComplianceConfig.SoftDeletedDocuments.");

    private void SkipUnlessHierarchies()
        => Assert.SkipUnless(theFixture.SupportsDocumentHierarchies,
            "This fixture does not replay DocumentComplianceConfig.SubClasses.");

    private void SkipUnlessConjoined()
        => Assert.SkipUnless(theFixture.SupportsConjoinedDocuments,
            "This store does not slice documents by tenant within one database (jasperfx#898).");

    private void SkipUnlessAllTenants()
        => Assert.SkipUnless(theFixture.SupportsDocumentDiagnosticAllTenants,
            "This store does not read every tenant; the all-tenants refusal fact covers it instead.");

    private void SkipUnlessCriteria()
        => Assert.SkipUnless(theFixture.SupportsDocumentDiagnosticCriteria,
            "This store does not apply Where / OrderBy; the refusal fact covers it instead.");

    private static ComplianceWidget Widget(string name, int weight = 1)
        => new() { Id = Guid.NewGuid(), Name = name, Color = "red", Weight = weight };

    private async Task<ComplianceWidget[]> PersistWidgetsAsync(int count)
    {
        var widgets = Enumerable.Range(1, count).Select(i => Widget($"w{i}", i)).ToArray();
        await PersistAsync(widgets).ConfigureAwait(false);
        return widgets;
    }

    private async Task PersistForAsync<T>(string tenantId, params T[] documents) where T : notnull
    {
        await using var session = LightweightSession(tenantId);
        session.Store(documents);
        await session.SaveChangesAsync(Cancellation).ConfigureAwait(false);
    }

    private Task<DocumentQueryResult> QueryAsync(string type, DocumentQueryOptions? options = null)
        => Diagnostics.QueryDocumentsAsync(type, options ?? new DocumentQueryOptions(1, 100), Cancellation);

    /// <summary>
    /// A member of raw stored JSON, found case-insensitively — the stores disagree on casing (Marten
    /// writes PascalCase by default, Polecat and Fisher camelCase), and the suite must not care.
    /// </summary>
    private static JsonNode? Member(string json, string name)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        return obj.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string WithMember(string json, string name, JsonNode? value)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        var key = obj.Select(x => x.Key).FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
                  ?? name;
        obj[key] = value;
        return obj.ToJsonString();
    }

    private static IEnumerable<Guid> IdsOf(DocumentQueryResult result)
        => result.Documents.Select(x => Guid.Parse(x.Id));

    // ---------------------------------------------------------------- identity (§2)

    [Fact]
    public void subject_is_a_stable_absolute_uri()
    {
        var subject = Diagnostics.Subject;

        subject.ShouldNotBeNull();
        subject.IsAbsoluteUri.ShouldBeTrue();
        Diagnostics.Subject.ShouldBe(subject);
    }

    [Fact]
    public void subject_pairs_with_the_stores_usage_source()
    {
        var diagnostics = Diagnostics;

        // The contract (jasperfx#870 §2) is equality with the same store's IDocumentStoreUsageSource.
        // Every current store implements both on its DocumentStore, which is also what Sessions is.
        var usageSource = diagnostics as IDocumentStoreUsageSource ?? theFixture.Sessions as IDocumentStoreUsageSource;
        Assert.SkipWhen(usageSource is null, "Neither the diagnostics nor the session factory is an IDocumentStoreUsageSource.");

        diagnostics.Subject.ShouldBe(usageSource!.Subject);
    }

    [Fact]
    public async Task document_types_are_listed_by_full_name_in_code()
    {
        var types = await Diagnostics.DocumentTypesAsync(Cancellation);

        types.Select(x => x.TypeName).ShouldContain(WidgetType);
        types.Select(x => x.TypeName).ShouldContain(GadgetType);
    }

    // ---------------------------------------------------------------- paging, metadata, identity (§3, §4)

    [Fact]
    public async Task pages_are_disjoint_stable_and_carry_the_total()
    {
        var widgets = await PersistWidgetsAsync(5);

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await QueryAsync(WidgetType, new DocumentQueryOptions(page, 2));

            result.TotalCount.ShouldBe(5);
            result.PageNumber.ShouldBe(page);
            result.PageSize.ShouldBe(2);
            result.Documents.Count.ShouldBe(page == 3 ? 1 : 2);

            seen.AddRange(IdsOf(result));
        }

        seen.ShouldBe(widgets.Select(x => x.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task query_results_carry_metadata_parallel_to_the_json()
    {
        var widget = Widget("metadata");
        await PersistAsync(widget);

        var result = await QueryAsync(WidgetType);

        result.DocumentsJson.Count.ShouldBe(1);
        var document = result.Documents.ShouldHaveSingleItem();

        document.Json.ShouldBe(result.DocumentsJson[0]);
        Guid.Parse(document.Id).ShouldBe(widget.Id);
        Member(document.Json, nameof(ComplianceWidget.Name))!.GetValue<string>().ShouldBe("metadata");

        document.Version.ShouldNotBeNullOrWhiteSpace();
        document.LastModified.ShouldNotBeNull();
        document.TenantId.ShouldNotBeNullOrWhiteSpace();
        document.IsDeleted.ShouldBeFalse();
        document.DeletedAt.ShouldBeNull();
        document.DocumentType.ShouldBe(WidgetType);
    }

    [Fact]
    public async Task a_new_write_changes_the_version()
    {
        var widget = Widget("before");
        await PersistAsync(widget);

        var before = (await QueryAsync(WidgetType)).Documents.Single().Version;

        widget.Name = "after";
        await PersistAsync(widget);

        var after = (await QueryAsync(WidgetType)).Documents.Single().Version;

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task id_equals_matches_an_upper_case_guid()
    {
        // The id arrives as text and is converted to the stored identity type (jasperfx#870 §3), so the
        // canonical-form trap — id::text = @id against an upper-case Guid — cannot bite.
        var widgets = await PersistWidgetsAsync(3);
        var target = widgets[1];

        var result = await QueryAsync(WidgetType,
            new DocumentQueryOptions(1, 10, target.Id.ToString().ToUpperInvariant()));

        result.TotalCount.ShouldBe(1);
        IdsOf(result).ShouldHaveSingleItem().ShouldBe(target.Id);
    }

    [Fact]
    public async Task id_equals_on_a_string_identity()
    {
        await PersistAsync(
            new ComplianceGadget { Id = "gadget-1", Kind = "a" },
            new ComplianceGadget { Id = "gadget-2", Kind = "b" });

        var result = await QueryAsync(GadgetType, new DocumentQueryOptions(1, 10, "gadget-2"));

        result.Documents.ShouldHaveSingleItem().Id.ShouldBe("gadget-2");
    }

    [Fact]
    public async Task load_returns_json_and_metadata()
    {
        var widget = Widget("loaded");
        await PersistAsync(widget);

        var document = await Diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation);

        document.ShouldNotBeNull();
        Guid.Parse(document.Id).ShouldBe(widget.Id);
        Member(document.Json, nameof(ComplianceWidget.Name))!.GetValue<string>().ShouldBe("loaded");
        document.Version.ShouldNotBeNullOrWhiteSpace();
        document.LastModified.ShouldNotBeNull();
        document.DocumentType.ShouldBe(WidgetType);
    }

    [Fact]
    public async Task load_matches_an_upper_case_guid()
    {
        var widget = Widget("upper");
        await PersistAsync(widget);

        var document = await Diagnostics.LoadDocumentAsync(
            WidgetType, widget.Id.ToString().ToUpperInvariant(), null, Cancellation);

        document.ShouldNotBeNull();
        Guid.Parse(document.Id).ShouldBe(widget.Id);
    }

    [Fact]
    public async Task load_of_a_missing_id_is_null()
    {
        await PersistWidgetsAsync(1);

        (await Diagnostics.LoadDocumentAsync(WidgetType, Guid.NewGuid().ToString(), null, Cancellation))
            .ShouldBeNull();
    }

    [Fact]
    public async Task the_legacy_json_load_still_answers()
    {
        var widget = Widget("legacy");
        await PersistAsync(widget);

        var json = await Diagnostics.LoadDocumentJsonAsync(WidgetType, widget.Id.ToString(), Cancellation);

        json.ShouldNotBeNull();
        Member(json, nameof(ComplianceWidget.Name))!.GetValue<string>().ShouldBe("legacy");
    }

    [Fact]
    public async Task an_unknown_type_reads_as_empty()
    {
        await PersistWidgetsAsync(2);

        var result = await QueryAsync("No.Such.Document");
        result.TotalCount.ShouldBe(0);
        result.Documents.ShouldBeEmpty();
        result.DocumentsJson.ShouldBeEmpty();

        (await Diagnostics.LoadDocumentAsync("No.Such.Document", Guid.NewGuid().ToString(), null, Cancellation))
            .ShouldBeNull();
    }

    // ---------------------------------------------------------------- soft deletes (§3)

    private async Task<(ComplianceTicket Live, ComplianceTicket Deleted)> LiveAndDeletedTicketsAsync()
    {
        var live = new ComplianceTicket { Id = Guid.NewGuid(), Title = "live", Priority = 1 };
        var deleted = new ComplianceTicket { Id = Guid.NewGuid(), Title = "deleted", Priority = 2 };
        await PersistAsync(live, deleted);

        await using var session = LightweightSession();
        session.Delete<ComplianceTicket>(deleted.Id);
        await session.SaveChangesAsync(Cancellation);

        return (live, deleted);
    }

    [Fact]
    public async Task soft_deleted_rows_are_excluded_by_default()
    {
        SkipUnlessSoftDeletes();
        var (live, _) = await LiveAndDeletedTicketsAsync();

        var result = await QueryAsync(TicketType);

        result.TotalCount.ShouldBe(1);
        IdsOf(result).ShouldHaveSingleItem().ShouldBe(live.Id);
    }

    [Fact]
    public async Task soft_deleted_rows_are_returned_flagged_when_asked_for()
    {
        SkipUnlessSoftDeletes();
        var (live, deleted) = await LiveAndDeletedTicketsAsync();

        var result = await QueryAsync(TicketType, new DocumentQueryOptions(1, 10) { IncludeSoftDeleted = true });

        result.TotalCount.ShouldBe(2);

        var byId = result.Documents.ToDictionary(x => Guid.Parse(x.Id));
        byId[live.Id].IsDeleted.ShouldBeFalse();
        byId[live.Id].DeletedAt.ShouldBeNull();
        byId[deleted.Id].IsDeleted.ShouldBeTrue();
        byId[deleted.Id].DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_load_by_id_returns_a_soft_deleted_row_flagged()
    {
        SkipUnlessSoftDeletes();
        var (_, deleted) = await LiveAndDeletedTicketsAsync();

        var document = await Diagnostics.LoadDocumentAsync(TicketType, deleted.Id.ToString(), null, Cancellation);

        document.ShouldNotBeNull();
        document.IsDeleted.ShouldBeTrue();
        document.DeletedAt.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------- hierarchies (§3)

    private async Task<(ComplianceVehicle Plain, ComplianceTruck Truck, ComplianceBus Bus)> VehiclesAsync()
    {
        var plain = new ComplianceVehicle { Id = Guid.NewGuid(), Make = "generic" };
        var truck = new ComplianceTruck { Id = Guid.NewGuid(), Make = "volvo", Axles = 3 };
        var bus = new ComplianceBus { Id = Guid.NewGuid(), Make = "mci", Seats = 50 };

        await using var session = LightweightSession();
        session.Store(plain);
        session.Store(truck);
        session.Store(bus);
        await session.SaveChangesAsync(Cancellation);

        return (plain, truck, bus);
    }

    [Fact]
    public async Task naming_a_sub_class_returns_only_that_sub_class()
    {
        SkipUnlessHierarchies();
        var (_, truck, _) = await VehiclesAsync();

        var result = await QueryAsync(TruckType);

        result.TotalCount.ShouldBe(1);
        var document = result.Documents.ShouldHaveSingleItem();
        Guid.Parse(document.Id).ShouldBe(truck.Id);
        document.DocumentType.ShouldBe(TruckType);
    }

    [Fact]
    public async Task naming_the_root_returns_every_row_with_its_own_type()
    {
        SkipUnlessHierarchies();
        var (plain, truck, bus) = await VehiclesAsync();

        var result = await QueryAsync(VehicleType);

        result.TotalCount.ShouldBe(3);
        var types = result.Documents.ToDictionary(x => Guid.Parse(x.Id), x => x.DocumentType);
        types[plain.Id].ShouldBe(VehicleType);
        types[truck.Id].ShouldBe(TruckType);
        types[bus.Id].ShouldBe(BusType);
    }

    [Fact]
    public async Task a_load_by_sub_class_name_does_not_return_a_sibling()
    {
        SkipUnlessHierarchies();
        var (_, truck, bus) = await VehiclesAsync();

        (await Diagnostics.LoadDocumentAsync(TruckType, bus.Id.ToString(), null, Cancellation)).ShouldBeNull();

        var loaded = await Diagnostics.LoadDocumentAsync(TruckType, truck.Id.ToString(), null, Cancellation);
        loaded.ShouldNotBeNull();
        loaded.DocumentType.ShouldBe(TruckType);

        // …and the root name reaches every row.
        (await Diagnostics.LoadDocumentAsync(VehicleType, bus.Id.ToString(), null, Cancellation))
            .ShouldNotBeNull()
            .DocumentType.ShouldBe(BusType);
    }

    // ---------------------------------------------------------------- tenancy (§3)

    private async Task SharedGadgetIdAcrossTenantsAsync()
    {
        // One id in three scopes — the (tenant, id) identity trap, as DocumentConjoinedTenancyCompliance.
        await PersistAsync(new ComplianceGadget { Id = "shared", Kind = "default" });
        await PersistForAsync(TenantA, new ComplianceGadget { Id = "shared", Kind = TenantA });
        await PersistForAsync(TenantB, new ComplianceGadget { Id = "shared", Kind = TenantB });
    }

    [Fact]
    public async Task a_named_tenant_reads_only_its_own_rows()
    {
        SkipUnlessConjoined();
        await SharedGadgetIdAcrossTenantsAsync();

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var result = await QueryAsync(GadgetType, new DocumentQueryOptions(1, 10) { TenantId = tenant });

            result.TotalCount.ShouldBe(1);
            var document = result.Documents.ShouldHaveSingleItem();
            Member(document.Json, nameof(ComplianceGadget.Kind))!.GetValue<string>().ShouldBe(tenant);
            document.TenantId.ShouldBe(tenant);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task no_tenant_means_the_default_tenant_only(string? tenantId)
    {
        // Not "all tenants" (Marten and Polecat before #870), and not a tenant named "" (CritterWatch#1304).
        SkipUnlessConjoined();
        await SharedGadgetIdAcrossTenantsAsync();

        var result = await QueryAsync(GadgetType, new DocumentQueryOptions(1, 10) { TenantId = tenantId });

        result.TotalCount.ShouldBe(1);
        Member(result.Documents.ShouldHaveSingleItem().Json, nameof(ComplianceGadget.Kind))!
            .GetValue<string>().ShouldBe("default");
    }

    [Fact]
    public async Task a_load_takes_a_tenant()
    {
        // The CritterWatch#1302 workaround — a one-row page query standing in for a tenant-less load —
        // is what this removes.
        SkipUnlessConjoined();
        await SharedGadgetIdAcrossTenantsAsync();

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var document = await Diagnostics.LoadDocumentAsync(GadgetType, "shared", tenant, Cancellation);

            document.ShouldNotBeNull();
            Member(document.Json, nameof(ComplianceGadget.Kind))!.GetValue<string>().ShouldBe(tenant);
            document.TenantId.ShouldBe(tenant);
        }

        var fallback = await Diagnostics.LoadDocumentAsync(GadgetType, "shared", "", Cancellation);
        Member(fallback.ShouldNotBeNull().Json, nameof(ComplianceGadget.Kind))!.GetValue<string>().ShouldBe("default");
    }

    // ---------------------------------------------------------------- all tenants (#928)

    private static DocumentQueryOptions AllTenants(int pageNumber = 1, int pageSize = 10)
        => new(pageNumber, pageSize) { AllTenants = true };

    private static string KindOf(StoredDocument document)
        => Member(document.Json, nameof(ComplianceGadget.Kind))!.GetValue<string>();

    [Fact]
    public async Task all_tenants_reads_every_tenants_row_for_a_shared_id()
    {
        SkipUnlessConjoined();
        SkipUnlessAllTenants();
        await SharedGadgetIdAcrossTenantsAsync();

        var result = await QueryAsync(GadgetType, AllTenants());

        // The same id in three tenants is three rows, each saying which tenant it came from.
        result.TotalCount.ShouldBe(3);
        result.Documents.Count.ShouldBe(3);
        result.Documents.ShouldAllBe(x => x.Id == "shared");
        result.Documents.Select(x => x.TenantId).Distinct().Count().ShouldBe(3);

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            KindOf(result.Documents.Single(x => x.TenantId == tenant)).ShouldBe(tenant);
        }

        // The third is the default tenant's row, under whatever id the store gives its default tenant.
        KindOf(result.Documents.Single(x => x.TenantId != TenantA && x.TenantId != TenantB)).ShouldBe("default");
    }

    [Fact]
    public async Task all_tenants_with_id_equals_returns_the_id_in_every_tenant()
    {
        SkipUnlessConjoined();
        SkipUnlessAllTenants();
        await SharedGadgetIdAcrossTenantsAsync();
        await PersistForAsync(TenantA, new ComplianceGadget { Id = "other", Kind = TenantA });

        var result = await QueryAsync(GadgetType, AllTenants() with { IdEquals = "shared" });

        result.TotalCount.ShouldBe(3);
        result.Documents.ShouldAllBe(x => x.Id == "shared");
    }

    [Fact]
    public async Task all_tenants_pages_do_not_repeat_rows_across_tenants()
    {
        SkipUnlessConjoined();
        SkipUnlessAllTenants();
        await SharedGadgetIdAcrossTenantsAsync();
        await PersistForAsync(TenantA, new ComplianceGadget { Id = "a2", Kind = TenantA });
        await PersistForAsync(TenantB, new ComplianceGadget { Id = "b2", Kind = TenantB });

        var seen = new List<(string? Tenant, string Id)>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await QueryAsync(GadgetType, AllTenants(page, 2));
            result.TotalCount.ShouldBe(5);
            seen.AddRange(result.Documents.Select(x => (x.TenantId, x.Id)));
        }

        seen.Count.ShouldBe(5);
        seen.Distinct().Count().ShouldBe(5);

        // Deterministic: the same page twice is the same rows in the same order.
        var again = await QueryAsync(GadgetType, AllTenants(2, 2));
        again.Documents.Select(x => (x.TenantId, x.Id)).ShouldBe(seen.Skip(2).Take(2));
    }

    [Fact]
    public async Task all_tenants_on_a_single_tenanted_type_reads_as_the_default()
    {
        _ = Diagnostics;
        SkipUnlessAllTenants();
        var widgets = await PersistWidgetsAsync(3);

        var all = await QueryAsync(WidgetType, AllTenants());
        var @default = await QueryAsync(WidgetType, new DocumentQueryOptions(1, 10));

        all.TotalCount.ShouldBe(3);
        IdsOf(all).ShouldBe(IdsOf(@default));
        IdsOf(all).ShouldBe(widgets.Select(x => x.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task all_tenants_with_a_named_tenant_is_a_contradiction()
    {
        _ = Diagnostics;
        await PersistWidgetsAsync(1);

        // Asserted whatever the store supports: one read cannot be both, and the store must not pick.
        await Should.ThrowAsync<ArgumentException>(
            () => QueryAsync(WidgetType, AllTenants() with { TenantId = TenantA }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task all_tenants_with_a_blank_tenant_is_not_a_contradiction(string? tenantId)
    {
        SkipUnlessAllTenants();
        await PersistWidgetsAsync(2);

        var result = await QueryAsync(WidgetType, AllTenants() with { TenantId = tenantId });
        result.TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task all_tenants_a_store_cannot_read_is_refused_not_narrowed()
    {
        _ = Diagnostics;
        SkipUnlessConjoined();
        Assert.SkipWhen(theFixture.SupportsDocumentDiagnosticAllTenants,
            "This store reads every tenant; the all-tenants facts cover it.");
        await SharedGadgetIdAcrossTenantsAsync();

        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(
            () => QueryAsync(GadgetType, AllTenants()));
        refused.Criterion.ShouldBe(nameof(DocumentQueryOptions.AllTenants));
    }

    // ---------------------------------------------------------------- criteria (§1)

    [Fact]
    public async Task criteria_a_store_cannot_apply_are_refused_not_ignored()
    {
        _ = Diagnostics;
        Assert.SkipWhen(theFixture.SupportsDocumentDiagnosticCriteria,
            "This store applies criteria; the filtering facts cover it.");

        await PersistWidgetsAsync(3);

        var where = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(
            () => QueryAsync(WidgetType, new DocumentQueryOptions(1, 10) { Where = "Weight > @0", Arguments = [1] }));
        where.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));

        var orderBy = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(
            () => QueryAsync(WidgetType, new DocumentQueryOptions(1, 10) { OrderBy = "Weight desc" }));
        orderBy.Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderBy));
    }

    [Fact]
    public async Task where_filters_the_page_and_the_total()
    {
        _ = Diagnostics;
        SkipUnlessCriteria();
        var widgets = await PersistWidgetsAsync(5);

        var result = await QueryAsync(WidgetType,
            new DocumentQueryOptions(1, 2) { Where = "Weight > @0", Arguments = [2], OrderBy = "Weight" });

        result.TotalCount.ShouldBe(3);
        IdsOf(result).ShouldBe([widgets[2].Id, widgets[3].Id]);
    }

    [Fact]
    public async Task where_on_a_string_member_with_a_typed_argument()
    {
        _ = Diagnostics;
        SkipUnlessCriteria();
        var widgets = await PersistWidgetsAsync(3);

        var result = await QueryAsync(WidgetType,
            new DocumentQueryOptions(1, 10) { Where = "Name = @0", Arguments = ["w2"] });

        IdsOf(result).ShouldHaveSingleItem().ShouldBe(widgets[1].Id);
    }

    [Fact]
    public async Task order_by_orders_the_page()
    {
        _ = Diagnostics;
        SkipUnlessCriteria();
        var widgets = await PersistWidgetsAsync(4);

        var result = await QueryAsync(WidgetType, new DocumentQueryOptions(1, 10) { OrderBy = "Weight desc" });

        IdsOf(result).ShouldBe(widgets.OrderByDescending(x => x.Weight).Select(x => x.Id));
    }

    [Fact]
    public async Task where_still_honors_soft_deletes()
    {
        _ = Diagnostics;
        SkipUnlessCriteria();
        SkipUnlessSoftDeletes();
        var (live, _) = await LiveAndDeletedTicketsAsync();

        // Both tickets match; only the live one is a live document.
        var result = await QueryAsync(TicketType,
            new DocumentQueryOptions(1, 10) { Where = "Priority > @0", Arguments = [0] });

        IdsOf(result).ShouldHaveSingleItem().ShouldBe(live.Id);
    }

    [Fact]
    public async Task an_unparsable_predicate_is_a_refusal()
    {
        _ = Diagnostics;
        SkipUnlessCriteria();
        await PersistWidgetsAsync(1);

        var ex = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(
            () => QueryAsync(WidgetType, new DocumentQueryOptions(1, 10) { Where = "Weight >>> nonsense (" }));
        ex.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
    }

    // ---------------------------------------------------------------- writes (§6)

    [Fact]
    public void the_writer_names_the_same_store_as_the_reader()
        => Writer.Subject.ShouldBe(Diagnostics.Subject);

    [Fact]
    public async Task save_inserts_a_document_the_application_can_load()
    {
        var writer = Writer;
        var template = Widget("template");
        await PersistAsync(template);
        var json = (await Diagnostics.LoadDocumentAsync(WidgetType, template.Id.ToString(), null, Cancellation))!.Json;

        var id = Guid.NewGuid();
        var newJson = WithMember(WithMember(json, "Id", id.ToString()), nameof(ComplianceWidget.Name), "inserted");

        var result = await writer.SaveDocumentJsonAsync(new DocumentWriteRequest(WidgetType, id.ToString(), newJson), Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.Saved);
        result.Document.ShouldNotBeNull().Version.ShouldNotBeNullOrWhiteSpace();

        await using var session = QuerySession();
        (await session.LoadAsync<ComplianceWidget>(id, Cancellation)).ShouldNotBeNull().Name.ShouldBe("inserted");
    }

    [Fact]
    public async Task save_replaces_and_returns_the_new_version()
    {
        var writer = Writer;
        var widget = Widget("original");
        await PersistAsync(widget);
        var before = (await Diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;

        var result = await writer.SaveDocumentJsonAsync(new DocumentWriteRequest(
            WidgetType, widget.Id.ToString(), WithMember(before.Json, nameof(ComplianceWidget.Name), "edited"))
        {
            ExpectedVersion = before.Version
        }, Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.Saved);
        result.Document.ShouldNotBeNull().Version.ShouldNotBe(before.Version);

        await using var session = QuerySession();
        (await session.LoadAsync<ComplianceWidget>(widget.Id, Cancellation))!.Name.ShouldBe("edited");
    }

    [Fact]
    public async Task a_stale_expected_version_is_refused_with_the_current_document()
    {
        var writer = Writer;
        var widget = Widget("original");
        await PersistAsync(widget);
        var stale = (await Diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;

        widget.Name = "changed by the application";
        await PersistAsync(widget);

        var result = await writer.SaveDocumentJsonAsync(new DocumentWriteRequest(
            WidgetType, widget.Id.ToString(), WithMember(stale.Json, nameof(ComplianceWidget.Name), "console edit"))
        {
            ExpectedVersion = stale.Version
        }, Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.ConcurrencyConflict);
        Member(result.Document.ShouldNotBeNull().Json, nameof(ComplianceWidget.Name))!
            .GetValue<string>().ShouldBe("changed by the application");

        await using var session = QuerySession();
        (await session.LoadAsync<ComplianceWidget>(widget.Id, Cancellation))!.Name.ShouldBe("changed by the application");
    }

    [Fact]
    public async Task an_id_that_disagrees_with_the_json_is_refused()
    {
        var writer = Writer;
        var widget = Widget("mismatch");
        await PersistAsync(widget);
        var json = (await Diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!.Json;

        await Should.ThrowAsync<ArgumentException>(() => writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest(WidgetType, Guid.NewGuid().ToString(), json), Cancellation));
    }

    [Fact]
    public async Task a_write_to_an_unknown_type_is_refused()
    {
        var writer = Writer;

        await Should.ThrowAsync<ArgumentException>(() => writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest("No.Such.Document", "1", "{}"), Cancellation));

        await Should.ThrowAsync<ArgumentException>(() => writer.DeleteDocumentAsync(
            new DocumentDeleteRequest("No.Such.Document", "1"), Cancellation));
    }

    [Fact]
    public async Task delete_removes_the_document()
    {
        var writer = Writer;
        var widget = Widget("doomed");
        await PersistAsync(widget);

        var result = await writer.DeleteDocumentAsync(new DocumentDeleteRequest(WidgetType, widget.Id.ToString()), Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.Deleted);

        await using var session = QuerySession();
        (await session.LoadAsync<ComplianceWidget>(widget.Id, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task delete_of_a_missing_document_is_not_found()
    {
        var writer = Writer;

        var result = await writer.DeleteDocumentAsync(
            new DocumentDeleteRequest(WidgetType, Guid.NewGuid().ToString()), Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.NotFound);
    }

    [Fact]
    public async Task delete_with_a_stale_version_is_refused()
    {
        var writer = Writer;
        var widget = Widget("original");
        await PersistAsync(widget);
        var stale = (await Diagnostics.LoadDocumentAsync(WidgetType, widget.Id.ToString(), null, Cancellation))!;

        widget.Name = "changed";
        await PersistAsync(widget);

        var result = await writer.DeleteDocumentAsync(
            new DocumentDeleteRequest(WidgetType, widget.Id.ToString()) { ExpectedVersion = stale.Version }, Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.ConcurrencyConflict);
        result.Document.ShouldNotBeNull();

        await using var session = QuerySession();
        (await session.LoadAsync<ComplianceWidget>(widget.Id, Cancellation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task delete_goes_through_the_pipeline_and_soft_deletes()
    {
        var writer = Writer;
        SkipUnlessSoftDeletes();
        var ticket = new ComplianceTicket { Id = Guid.NewGuid(), Title = "soft", Priority = 1 };
        await PersistAsync(ticket);

        var result = await writer.DeleteDocumentAsync(new DocumentDeleteRequest(TicketType, ticket.Id.ToString()), Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.Deleted);

        var document = await Diagnostics.LoadDocumentAsync(TicketType, ticket.Id.ToString(), null, Cancellation);
        document.ShouldNotBeNull().IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task a_save_lands_in_the_requested_tenant_only()
    {
        var writer = Writer;
        SkipUnlessConjoined();
        await SharedGadgetIdAcrossTenantsAsync();
        var json = (await Diagnostics.LoadDocumentAsync(GadgetType, "shared", TenantA, Cancellation))!.Json;

        var result = await writer.SaveDocumentJsonAsync(new DocumentWriteRequest(
            GadgetType, "shared", WithMember(json, nameof(ComplianceGadget.Kind), "edited")) { TenantId = TenantA },
            Cancellation);

        result.Status.ShouldBe(DocumentWriteStatus.Saved);

        await using (var a = QuerySession(TenantA))
        {
            (await a.LoadAsync<ComplianceGadget>("shared", Cancellation))!.Kind.ShouldBe("edited");
        }

        await using (var b = QuerySession(TenantB))
        {
            (await b.LoadAsync<ComplianceGadget>("shared", Cancellation))!.Kind.ShouldBe(TenantB);
        }

        await using var fallback = QuerySession();
        (await fallback.LoadAsync<ComplianceGadget>("shared", Cancellation))!.Kind.ShouldBe("default");
    }
}
