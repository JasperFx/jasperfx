using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Multi-stream projection events and aggregate

public record EmployeeHired(string Department, string Name);

public record EmployeeDeparted(string Department, string Name);

/// <summary>
/// Carries several identities in one event, for <c>Identities&lt;T&gt;</c>.
/// </summary>
public record DepartmentsAudited(string[] Departments);

/// <summary>
/// The child item a <c>TeamOnboarded</c> fans out into. It carries no identity of its own on
/// purpose: a fanned-out child is applied within the slice its parent was grouped into.
/// </summary>
public record TeamMemberOnboarded(string Name);

public record TeamOnboarded(string Department, TeamMemberOnboarded[] Members);

public class ComplianceDepartment
{
    public string Id { get; set; } = string.Empty;
    public int HeadCount { get; set; }
    public List<string> Names { get; set; } = new();
    public int AuditCount { get; set; }
}

/// <summary>
/// The multi-stream projection under compliance. Every grouping construct it uses —
/// <c>Identity</c>, <c>Identities</c>, <c>FanOut</c> — is declared on JasperFx's shared
/// <c>JasperFxMultiStreamProjectionBase</c>; only the concrete base class name is per-product, and
/// that arrives through the <c>ComplianceMultiStreamProjectionBase</c> global alias.
/// </summary>
public partial class ComplianceDepartmentProjection: ComplianceMultiStreamProjectionBase
{
    public ComplianceDepartmentProjection()
    {
        Identity<EmployeeHired>(x => x.Department);
        Identity<EmployeeDeparted>(x => x.Department);
        Identities<DepartmentsAudited>(x => x.Departments);

        // One event explodes into a child per member. Grouping comes from the parent -- the child is
        // applied inside whatever slice TeamOnboarded landed in -- so the child needs no identity.
        Identity<TeamOnboarded>(x => x.Department);
        FanOut<TeamOnboarded, TeamMemberOnboarded>(x => x.Members);
    }

    public void Apply(EmployeeHired e, ComplianceDepartment doc)
    {
        doc.HeadCount++;
        doc.Names.Add(e.Name);
    }

    public void Apply(EmployeeDeparted e, ComplianceDepartment doc)
    {
        doc.HeadCount--;
        doc.Names.Remove(e.Name);
    }

    public void Apply(DepartmentsAudited e, ComplianceDepartment doc) => doc.AuditCount++;

    public void Apply(TeamMemberOnboarded e, ComplianceDepartment doc)
    {
        doc.HeadCount++;
        doc.Names.Add(e.Name);
    }
}

#endregion

/// <summary>
/// Multi-stream projections — the grouping half of the projection model, where a projection decides
/// which aggregate each event belongs to rather than inheriting that from the stream.
/// </summary>
/// <remarks>
/// <para>
/// The most valuable projection surface to pin cross-store, because slicing is where two
/// implementations have the most freedom to disagree: which shard sees which event, whether one
/// event can reach several aggregates, and whether a fanned-out child is routed by its own identity
/// or its parent's. Single-stream aggregation has none of those degrees of freedom.
/// </para>
/// <para>
/// Everything here runs through <c>JasperFxMultiStreamProjectionBase</c>, so the only per-consumer
/// cost is the <c>ComplianceMultiStreamProjectionBase</c> alias — the same mechanism the string
/// identity suite already uses, and unlike the flat-table suite no constructor shim is needed
/// because both products' multi-stream bases are parameterless.
/// </para>
/// <para>
/// Grouping is asserted through the *persisted* document, never by re-folding, so a projection that
/// sliced correctly but never wrote fails.
/// </para>
/// </remarks>
public abstract class MultiStreamProjectionCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_multi_stream";

        config.AddEventType<EmployeeHired>();
        config.AddEventType<EmployeeDeparted>();
        config.AddEventType<DepartmentsAudited>();
        config.AddEventType<TeamOnboarded>();

        config.AddProjection(new ComplianceDepartmentProjection(), ProjectionLifecycle.Inline);
    };

    /// <summary>
    /// The same projection registered <see cref="ProjectionLifecycle.Async"/>, in the same schema.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = "compliance_multi_stream";

        config.AddEventType<EmployeeHired>();
        config.AddEventType<EmployeeDeparted>();
        config.AddEventType<DepartmentsAudited>();
        config.AddEventType<TeamOnboarded>();

        config.AddProjection(new ComplianceDepartmentProjection(), ProjectionLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Departments are suffixed per test so sibling tests sharing the schema cannot collide.
    /// </summary>
    private static string department(string name) => $"{name}-{Guid.NewGuid():N}";

    private async Task appendAsync(params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).StartStream(Guid.NewGuid(), events);
        await SaveChangesAsync(session);
    }

    private async Task<ComplianceDepartment?> departmentAsync(string id)
    {
        await using var query = OpenSession();
        return await LoadDocumentAsync<ComplianceDepartment>(query, id);
    }

    [Fact]
    public async Task events_from_separate_streams_land_on_one_aggregate()
    {
        var dept = department("engineering");

        // Three separate streams, one aggregate -- the whole point of a multi-stream projection.
        await appendAsync(new EmployeeHired(dept, "Ada"));
        await appendAsync(new EmployeeHired(dept, "Grace"));
        await appendAsync(new EmployeeHired(dept, "Edsger"));

        var doc = await departmentAsync(dept);

        doc.ShouldNotBeNull();
        doc.Id.ShouldBe(dept);
        doc.HeadCount.ShouldBe(3);
        doc.Names.OrderBy(x => x).ToArray().ShouldBe(new[] { "Ada", "Edsger", "Grace" });
    }

    [Fact]
    public async Task events_within_one_stream_are_grouped_by_their_identity()
    {
        var left = department("sales");
        var right = department("support");

        // A single stream carrying events for two different aggregates.
        await appendAsync(
            new EmployeeHired(left, "Ada"),
            new EmployeeHired(right, "Grace"),
            new EmployeeHired(left, "Edsger"));

        (await departmentAsync(left))!.HeadCount.ShouldBe(2);
        (await departmentAsync(right))!.HeadCount.ShouldBe(1);
    }

    [Fact]
    public async Task separate_identities_stay_independent()
    {
        var left = department("alpha");
        var right = department("beta");

        await appendAsync(new EmployeeHired(left, "Ada"), new EmployeeHired(right, "Grace"));
        await appendAsync(new EmployeeDeparted(left, "Ada"));

        (await departmentAsync(left))!.HeadCount.ShouldBe(0);

        var other = await departmentAsync(right);
        other!.HeadCount.ShouldBe(1);
        other.Names.ShouldBe(new[] { "Grace" });
    }

    [Fact]
    public async Task an_event_can_update_an_aggregate_built_by_an_earlier_commit()
    {
        var dept = department("platform");

        await appendAsync(new EmployeeHired(dept, "Ada"), new EmployeeHired(dept, "Grace"));
        await appendAsync(new EmployeeDeparted(dept, "Ada"));

        var doc = await departmentAsync(dept);

        doc!.HeadCount.ShouldBe(1);
        doc.Names.ShouldBe(new[] { "Grace" });
    }

    [Fact]
    public async Task one_event_reaches_every_identity_it_names()
    {
        var left = department("finance");
        var right = department("legal");

        await appendAsync(new EmployeeHired(left, "Ada"), new EmployeeHired(right, "Grace"));

        // Identities<T> -- a single event applied to two different aggregates.
        await appendAsync(new DepartmentsAudited([left, right]));

        (await departmentAsync(left))!.AuditCount.ShouldBe(1);
        (await departmentAsync(right))!.AuditCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_fanned_out_event_applies_each_child_inside_its_parents_identity()
    {
        var left = department("research");
        var right = department("design");

        // One event per department, each exploding into a child per member. Each child is applied
        // as if it were its own event, but inside the slice its parent was grouped into.
        await appendAsync(
            new TeamOnboarded(left, [new TeamMemberOnboarded("Ada"), new TeamMemberOnboarded("Grace")]),
            new TeamOnboarded(right, [new TeamMemberOnboarded("Edsger")]));

        var first = await departmentAsync(left);
        first.ShouldNotBeNull();
        first.HeadCount.ShouldBe(2);
        first.Names.OrderBy(x => x).ToArray().ShouldBe(new[] { "Ada", "Grace" });

        var second = await departmentAsync(right);
        second.ShouldNotBeNull();
        second.HeadCount.ShouldBe(1);
        second.Names.ShouldBe(new[] { "Edsger" });
    }

    [Fact]
    public async Task a_fanned_out_child_composes_with_ordinary_events_on_the_same_aggregate()
    {
        var dept = department("mixed");

        await appendAsync(new EmployeeHired(dept, "Ada"));
        await appendAsync(new TeamOnboarded(dept, [new TeamMemberOnboarded("Grace")]));
        await appendAsync(new EmployeeDeparted(dept, "Ada"));

        var doc = await departmentAsync(dept);

        doc.ShouldNotBeNull();
        doc.HeadCount.ShouldBe(1);
        doc.Names.ShouldBe(new[] { "Grace" });
    }

    [Fact]
    public async Task an_identity_that_no_event_names_has_no_aggregate()
    {
        await appendAsync(new EmployeeHired(department("ops"), "Ada"));

        (await departmentAsync(department("never-referenced"))).ShouldBeNull();
    }

    [Fact]
    public async Task the_async_lifecycle_produces_the_same_aggregate_as_inline()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var dept = department("async-eng");

        await appendAsync(new EmployeeHired(dept, "Ada"));
        await appendAsync(new EmployeeHired(dept, "Grace"), new EmployeeDeparted(dept, "Ada"));

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var doc = await departmentAsync(dept);

        doc.ShouldNotBeNull();
        doc.HeadCount.ShouldBe(1);
        doc.Names.ShouldBe(new[] { "Grace" });
    }

    [Fact]
    public async Task a_rebuild_reproduces_the_grouping()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var left = department("rebuild-a");
        var right = department("rebuild-b");

        await appendAsync(new EmployeeHired(left, "Ada"), new EmployeeHired(right, "Grace"));
        await appendAsync(new TeamOnboarded(left, [new TeamMemberOnboarded("Edsger")]));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await departmentAsync(left))!.HeadCount.ShouldBe(2);

        await daemon.RebuildProjectionAsync<ComplianceDepartment>(Cancellation);

        // Exactly once, not twice -- a rebuild that replayed onto the surviving document rather than
        // starting from empty would double every count here.
        var rebuilt = await departmentAsync(left);
        rebuilt.ShouldNotBeNull();
        rebuilt.HeadCount.ShouldBe(2);
        rebuilt.Names.OrderBy(x => x).ToArray().ShouldBe(new[] { "Ada", "Edsger" });

        (await departmentAsync(right))!.HeadCount.ShouldBe(1);
    }
}
