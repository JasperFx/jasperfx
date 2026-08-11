using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentReadOperations.Query{T}" /> and the
/// <see cref="DocumentQueryableExtensions" /> terminators.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not a LINQ conformance suite, and must not become one.</strong> The Critter Stack
/// position — stated in this library's README and unchanged — is that LINQ provider behavior is out
/// of shared-compliance scope permanently, because the stores' query languages diverge structurally
/// enough that a broad shared suite would pin coincidence rather than contract.
/// </para>
/// <para>
/// What is asserted here is narrower and different in kind: the <em>minimum translatable set</em>
/// that <see cref="IDocumentReadOperations.Query{T}" /> promises. A store-agnostic consumer holding
/// only <c>IQueryable&lt;T&gt;</c> has no way to discover whether <c>OrderBy</c> is translated
/// server-side or silently unsupported, so the operators that contract covers — <c>Where</c>,
/// <c>Select</c>, <c>OrderBy</c> / <c>OrderByDescending</c>, <c>ThenBy</c>, <c>Take</c>,
/// <c>Skip</c>, <c>Distinct</c> — are pinned. They are exactly the operators the measured consumer
/// surface uses (jasperfx#647). Anything beyond them stays product-owned; a store is free to support
/// more and is never held to it here.
/// </para>
/// <para>
/// <see cref="a_queryable_composes_across_statements" /> pins the shape rather than an operator, and
/// is why <c>Query&lt;T&gt;()</c> returns a real <see cref="IQueryable{T}" /> instead of a fluent
/// builder: real consumer code conditionally adds clauses to a query held in a local.
/// </para>
/// </remarks>
public abstract class DocumentQueryCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_documents";
        config.AddDocumentType<ComplianceWidget>();
        config.AddDocumentType<ComplianceGadget>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    private Task theWidgetsAsync()
    {
        return PersistAsync(
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "alpha", Color = "red", Weight = 1 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "bravo", Color = "red", Weight = 2 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "charlie", Color = "blue", Weight = 3 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "delta", Color = "blue", Weight = 4 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "echo", Color = "green", Weight = 5 });
    }

    [Fact]
    public async Task query_returns_every_committed_document()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();
        var all = await query.Query<ComplianceWidget>().ToListAsync(Cancellation);

        all.Count.ShouldBe(5);
    }

    [Fact]
    public async Task query_over_an_empty_document_type_returns_an_empty_list()
    {
        await using var query = QuerySession();
        var all = await query.Query<ComplianceWidget>().ToListAsync(Cancellation);

        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task where_filters()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();
        var red = await query.Query<ComplianceWidget>().Where(x => x.Color == "red").ToListAsync(Cancellation);

        red.Count.ShouldBe(2);
        red.Select(x => x.Name).OrderBy(x => x).ShouldBe(new[] { "alpha", "bravo" });
    }

    [Fact]
    public async Task where_over_a_captured_collection()
    {
        await theWidgetsAsync();

        var wanted = new[] { "alpha", "echo" };

        await using var query = QuerySession();
        var matches = await query.Query<ComplianceWidget>()
            .Where(x => wanted.Contains(x.Name))
            .ToListAsync(Cancellation);

        matches.Count.ShouldBe(2);
        matches.Select(x => x.Name).OrderBy(x => x).ShouldBe(new[] { "alpha", "echo" });
    }

    [Fact]
    public async Task select_projects_to_a_scalar()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();
        var names = await query.Query<ComplianceWidget>()
            .Where(x => x.Color == "blue")
            .Select(x => x.Name)
            .ToListAsync(Cancellation);

        names.OrderBy(x => x).ShouldBe(new[] { "charlie", "delta" });
    }

    [Fact]
    public async Task order_by_and_order_by_descending()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var ascending = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Weight)
            .Select(x => x.Weight)
            .ToListAsync(Cancellation);
        ascending.ShouldBe(new[] { 1, 2, 3, 4, 5 });

        var descending = await query.Query<ComplianceWidget>()
            .OrderByDescending(x => x.Weight)
            .Select(x => x.Weight)
            .ToListAsync(Cancellation);
        descending.ShouldBe(new[] { 5, 4, 3, 2, 1 });
    }

    [Fact]
    public async Task then_by_breaks_ties()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var descendingTieBreak = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Color)
            .ThenByDescending(x => x.Weight)
            .Select(x => x.Name)
            .ToListAsync(Cancellation);
        descendingTieBreak.ShouldBe(new[] { "delta", "charlie", "echo", "bravo", "alpha" });

        var ascendingTieBreak = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Color)
            .ThenBy(x => x.Weight)
            .Select(x => x.Name)
            .ToListAsync(Cancellation);
        ascendingTieBreak.ShouldBe(new[] { "charlie", "delta", "echo", "alpha", "bravo" });
    }

    [Fact]
    public async Task take_and_skip_page_a_result_set()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var firstPage = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Weight)
            .Take(2)
            .Select(x => x.Weight)
            .ToListAsync(Cancellation);
        firstPage.ShouldBe(new[] { 1, 2 });

        var secondPage = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Weight)
            .Skip(2)
            .Take(2)
            .Select(x => x.Weight)
            .ToListAsync(Cancellation);
        secondPage.ShouldBe(new[] { 3, 4 });
    }

    [Fact]
    public async Task distinct_over_a_projected_scalar()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();
        var colors = await query.Query<ComplianceWidget>()
            .Select(x => x.Color)
            .Distinct()
            .ToListAsync(Cancellation);

        colors.OrderBy(x => x).ShouldBe(new[] { "blue", "green", "red" });
    }

    [Fact]
    public async Task first_or_default_hits_and_misses()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var hit = await query.Query<ComplianceWidget>()
            .Where(x => x.Name == "charlie")
            .FirstOrDefaultAsync(Cancellation);
        hit.ShouldNotBeNull();
        hit.Weight.ShouldBe(3);

        var miss = await query.Query<ComplianceWidget>()
            .Where(x => x.Name == "zulu")
            .FirstOrDefaultAsync(Cancellation);
        miss.ShouldBeNull();
    }

    [Fact]
    public async Task first_or_default_with_a_predicate()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var hit = await query.Query<ComplianceWidget>().FirstOrDefaultAsync(x => x.Name == "delta", Cancellation);
        hit.ShouldNotBeNull();
        hit.Weight.ShouldBe(4);

        (await query.Query<ComplianceWidget>().FirstOrDefaultAsync(x => x.Name == "zulu", Cancellation))
            .ShouldBeNull();
    }

    [Fact]
    public async Task first_or_default_respects_the_ordering()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        var lightest = await query.Query<ComplianceWidget>()
            .OrderBy(x => x.Weight)
            .FirstOrDefaultAsync(Cancellation);
        lightest.ShouldNotBeNull();
        lightest.Name.ShouldBe("alpha");

        var heaviest = await query.Query<ComplianceWidget>()
            .OrderByDescending(x => x.Weight)
            .FirstOrDefaultAsync(Cancellation);
        heaviest.ShouldNotBeNull();
        heaviest.Name.ShouldBe("echo");
    }

    [Fact]
    public async Task count_with_and_without_a_predicate()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        (await query.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(5);
        (await query.Query<ComplianceWidget>().CountAsync(x => x.Color == "red", Cancellation)).ShouldBe(2);
        (await query.Query<ComplianceWidget>().Where(x => x.Weight > 3).CountAsync(Cancellation)).ShouldBe(2);
        (await query.Query<ComplianceWidget>().CountAsync(x => x.Color == "chartreuse", Cancellation)).ShouldBe(0);
    }

    [Fact]
    public async Task any_with_and_without_a_predicate()
    {
        await theWidgetsAsync();

        await using var query = QuerySession();

        (await query.Query<ComplianceWidget>().AnyAsync(Cancellation)).ShouldBeTrue();
        (await query.Query<ComplianceWidget>().AnyAsync(x => x.Color == "green", Cancellation)).ShouldBeTrue();
        (await query.Query<ComplianceWidget>().AnyAsync(x => x.Color == "chartreuse", Cancellation)).ShouldBeFalse();
        (await query.Query<ComplianceGadget>().AnyAsync(Cancellation)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_queryable_composes_across_statements()
    {
        await theWidgetsAsync();

        await using var session = QuerySession();

        // The shape real consumer code uses: hold the queryable, add clauses conditionally, and
        // terminate once. Nothing here would compile against a fluent builder.
        IQueryable<ComplianceWidget> query = session.Query<ComplianceWidget>();

        string? colorFilter = "red";
        int? minimumWeight = 2;

        if (colorFilter != null)
        {
            query = query.Where(x => x.Color == colorFilter);
        }

        if (minimumWeight != null)
        {
            query = query.Where(x => x.Weight >= minimumWeight.Value);
        }

        var results = await query.OrderByDescending(x => x.Weight).Take(10).ToListAsync(Cancellation);

        results.Count.ShouldBe(1);
        results.Single().Name.ShouldBe("bravo");
    }

    [Fact]
    public async Task a_queryable_can_be_narrowed_by_a_shared_helper()
    {
        await theWidgetsAsync();

        await using var session = QuerySession();

        var heavy = await OnlyHeavierThan(session.Query<ComplianceWidget>(), 3).ToListAsync(Cancellation);

        heavy.Count.ShouldBe(2);
        heavy.Select(x => x.Name).OrderBy(x => x).ShouldBe(new[] { "delta", "echo" });
    }

    /// <summary>
    /// Consumers factor shared predicates into helpers that take and return
    /// <see cref="IQueryable{T}" />. That only works if <c>Query&lt;T&gt;()</c> hands back the real
    /// interface.
    /// </summary>
    private static IQueryable<ComplianceWidget> OnlyHeavierThan(IQueryable<ComplianceWidget> source, int weight)
        => source.Where(x => x.Weight > weight);

    [Fact]
    public async Task query_does_not_see_uncommitted_writes_from_another_session()
    {
        await theWidgetsAsync();

        await using var writer = LightweightSession();
        writer.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "foxtrot", Color = "red", Weight = 6 });

        await using var query = QuerySession();
        var names = await query.Query<ComplianceWidget>().Select(x => x.Name).ToListAsync(Cancellation);

        names.ShouldNotContain("foxtrot");
        names.Count.ShouldBe(5);
    }
}
