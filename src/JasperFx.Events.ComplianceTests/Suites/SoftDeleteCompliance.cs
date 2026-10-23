using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Soft deletes, held to one definition across stores (jasperfx#785 §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Soft deletes stay off the session <em>contract</em>: nothing here widens it. The declarative half is
/// already shared (<see cref="JasperFx.Metadata.ISoftDeleted" />, and <c>DeleteWhere</c> is on
/// <see cref="IDocumentWriteOperations" />), and the rest is reached through three fixture seam members
/// with throwing defaults: <see cref="DocumentStorageComplianceFixture.QuerySoftDeletedAsync{T}" /> for the
/// four marker operators, <see cref="DocumentStorageComplianceFixture.UndoDeleteWhere{T}" /> and
/// <see cref="DocumentStorageComplianceFixture.HardDeleteWhere{T}" />. Gated on
/// <see cref="DocumentStorageComplianceFixture.SupportsSoftDeleteOperators" /> (default false).
/// </para>
/// <para>
/// The survey behind #785 found three stores that had converged on these semantics by separate routes,
/// with nothing holding them there. The load-bearing fact is the last: <b>a soft-delete operator against
/// a type that is not soft-deleted is refused</b>. There is no column to answer from, so an
/// <c>IsDeleted()</c> that came back with every row, or a <c>DeletedSince()</c> that was silently dropped,
/// would both look like real answers (polecat#558).
/// </para>
/// <para>
/// Timestamps are compared with minutes of margin rather than to the tick: <c>deleted_at</c> is
/// <c>timestamptz</c> on Marten, <c>datetimeoffset</c> on Polecat and millisecond ISO-8601 text on Fisher,
/// stamped by the database's clock, which need not agree with the test's.
/// </para>
/// </remarks>
public abstract class SoftDeleteCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly TimeSpan _margin = TimeSpan.FromMinutes(5);

    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_soft_deletes";
        config.AddDocumentType<ComplianceArchivable>();
        config.AddDocumentType<ComplianceWidget>();
        config.SoftDeleted<ComplianceArchivable>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);

        if (!theFixture.SupportsSoftDeleteOperators)
        {
            return;
        }

        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanDocumentDataAsync().ConfigureAwait(false);
    }

    private void SkipUnlessSupported()
        => Assert.SkipUnless(theFixture.SupportsSoftDeleteOperators,
            "This store does not implement the soft-delete fixture seam (jasperfx#785).");

    private static ComplianceArchivable Archivable(string title) => new() { Id = Guid.NewGuid(), Title = title };

    private async Task<IReadOnlyList<T>> QueryAsync<T>(SoftDeleteFilter filter) where T : notnull
    {
        await using var query = QuerySession();
        return await theFixture.QuerySoftDeletedAsync<T>(query, filter, Cancellation);
    }

    private async Task DeleteAsync(params ComplianceArchivable[] documents)
    {
        await using var session = LightweightSession();
        foreach (var document in documents) session.Delete<ComplianceArchivable>(document.Id);
        await session.SaveChangesAsync(Cancellation);
    }

    private async Task<(ComplianceArchivable Live, ComplianceArchivable Deleted)> LiveAndDeletedAsync()
    {
        var live = Archivable("live");
        var deleted = Archivable("deleted");
        await PersistAsync(live, deleted);
        await DeleteAsync(deleted);
        return (live, deleted);
    }

    private static Guid[] Ids(IEnumerable<ComplianceArchivable> documents)
        => documents.Select(x => x.Id).OrderBy(x => x).ToArray();

    /// <summary>
    /// A soft-deleted document loaded by id: null on a store that excludes it from loads, and on one that
    /// does not (<see cref="DocumentStorageComplianceFixture.LoadExcludesSoftDeletedDocuments" />), the
    /// document with its <see cref="JasperFx.Metadata.ISoftDeleted" /> members saying it is deleted. Never a
    /// document that looks live.
    /// </summary>
    private async Task ShouldLoadAsDeletedAsync(Guid id)
    {
        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceArchivable>(id, Cancellation);

        if (theFixture.LoadExcludesSoftDeletedDocuments)
        {
            loaded.ShouldBeNull();
            return;
        }

        loaded.ShouldNotBeNull();
        loaded.Deleted.ShouldBeTrue();
        loaded.DeletedAt.ShouldNotBeNull();
    }

    // ------------------------------------------------------------ implicit exclusion

    [Fact]
    public async Task a_soft_deleted_document_does_not_load_as_live()
    {
        SkipUnlessSupported();
        var (live, deleted) = await LiveAndDeletedAsync();

        await ShouldLoadAsDeletedAsync(deleted.Id);

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceArchivable>(live.Id, Cancellation);
        loaded.ShouldNotBeNull().Deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task a_soft_deleted_document_is_excluded_from_query_and_count()
    {
        SkipUnlessSupported();
        var (live, _) = await LiveAndDeletedAsync();

        await using var query = QuerySession();
        Ids(await query.Query<ComplianceArchivable>().ToListAsync(Cancellation)).ShouldBe([live.Id]);
        (await query.Query<ComplianceArchivable>().CountAsync(Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task delete_where_soft_deletes()
    {
        SkipUnlessSupported();
        var doomed = Archivable("doomed");
        await PersistAsync(doomed, Archivable("kept"));

        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceArchivable>(x => x.Title == "doomed");
            await session.SaveChangesAsync(Cancellation);
        }

        // Gone from a plain read, still there to be found by an operator.
        await ShouldLoadAsDeletedAsync(doomed.Id);
        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.IsDeleted)).ShouldBe([doomed.Id]);
    }

    // ------------------------------------------------------------ the operators

    [Fact]
    public async Task maybe_deleted_returns_live_and_deleted_rows()
    {
        SkipUnlessSupported();
        var (live, deleted) = await LiveAndDeletedAsync();

        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.MaybeDeleted))
            .ShouldBe(Ids([live, deleted]));
    }

    [Fact]
    public async Task is_deleted_returns_only_deleted_rows()
    {
        SkipUnlessSupported();
        var (_, deleted) = await LiveAndDeletedAsync();

        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.IsDeleted)).ShouldBe([deleted.Id]);
    }

    [Fact]
    public async Task deleted_since_and_before_bracket_the_deletion()
    {
        SkipUnlessSupported();
        var (_, deleted) = await LiveAndDeletedAsync();
        var now = DateTimeOffset.UtcNow;

        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.DeletedSince(now - _margin))).ShouldBe([deleted.Id]);
        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.DeletedSince(now + _margin))).ShouldBeEmpty();

        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.DeletedBefore(now + _margin))).ShouldBe([deleted.Id]);
        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.DeletedBefore(now - _margin))).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_soft_deleted_members_are_populated_on_read()
    {
        SkipUnlessSupported();
        var (live, deleted) = await LiveAndDeletedAsync();

        var byId = (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.MaybeDeleted)).ToDictionary(x => x.Id);

        byId[deleted.Id].Deleted.ShouldBeTrue();
        byId[deleted.Id].DeletedAt.ShouldNotBeNull();
        byId[live.Id].Deleted.ShouldBeFalse();
        byId[live.Id].DeletedAt.ShouldBeNull();
    }

    // ------------------------------------------------------------ deleting again, undeleting, hard deletes

    [Fact]
    public async Task deleting_an_already_deleted_row_does_not_restamp_it()
    {
        SkipUnlessSupported();
        var (_, deleted) = await LiveAndDeletedAsync();
        var first = (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.IsDeleted)).Single().DeletedAt;

        // DeleteWhere only: all three stores guard their criteria delete (polecat#419). The by-id path has
        // not been surveyed across stores, so it is not pinned here.
        await Task.Delay(50, Cancellation);
        await using (var session = LightweightSession())
        {
            session.DeleteWhere<ComplianceArchivable>(x => x.Id == deleted.Id);
            await session.SaveChangesAsync(Cancellation);
        }

        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.IsDeleted)).Single().DeletedAt.ShouldBe(first);
    }

    [Fact]
    public async Task undo_delete_where_restores_and_clears_the_timestamp()
    {
        SkipUnlessSupported();
        var (live, deleted) = await LiveAndDeletedAsync();

        await using (var session = LightweightSession())
        {
            theFixture.UndoDeleteWhere<ComplianceArchivable>(session, x => x.Id == deleted.Id);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var restored = await query.LoadAsync<ComplianceArchivable>(deleted.Id, Cancellation);
        restored.ShouldNotBeNull();
        restored.Deleted.ShouldBeFalse();
        restored.DeletedAt.ShouldBeNull();

        // Cleared, not merely flipped: a restored row cannot be found again by DeletedSince
        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.IsDeleted)).ShouldBeEmpty();
        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.DeletedSince(DateTimeOffset.UtcNow - _margin)))
            .ShouldBeEmpty();
        Ids(await query.Query<ComplianceArchivable>().ToListAsync(Cancellation)).ShouldBe(Ids([live, deleted]));
    }

    [Fact]
    public async Task hard_delete_where_removes_a_soft_deleted_row()
    {
        SkipUnlessSupported();
        var (live, deleted) = await LiveAndDeletedAsync();

        await using (var session = LightweightSession())
        {
            theFixture.HardDeleteWhere<ComplianceArchivable>(session, x => x.Id == deleted.Id);
            await session.SaveChangesAsync(Cancellation);
        }

        Ids(await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.MaybeDeleted)).ShouldBe([live.Id]);
    }

    [Fact]
    public async Task hard_delete_where_removes_a_live_row_outright()
    {
        SkipUnlessSupported();
        var live = Archivable("live");
        await PersistAsync(live);

        await using (var session = LightweightSession())
        {
            theFixture.HardDeleteWhere<ComplianceArchivable>(session, x => x.Id == live.Id);
            await session.SaveChangesAsync(Cancellation);
        }

        (await QueryAsync<ComplianceArchivable>(SoftDeleteFilter.MaybeDeleted)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------ the load-bearing refusal

    [Theory]
    [InlineData(SoftDeleteOperator.MaybeDeleted)]
    [InlineData(SoftDeleteOperator.IsDeleted)]
    [InlineData(SoftDeleteOperator.DeletedSince)]
    [InlineData(SoftDeleteOperator.DeletedBefore)]
    public async Task an_operator_against_a_type_that_is_not_soft_deleted_is_refused(SoftDeleteOperator op)
    {
        SkipUnlessSupported();
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "hard" });

        // Refused, in whatever exception the store's parser uses. IsDeleted() returning every row, or
        // DeletedSince() dropped silently, are the answers this exists to rule out.
        await Should.ThrowAsync<Exception>(() =>
            QueryAsync<ComplianceWidget>(new SoftDeleteFilter(op, DateTimeOffset.UtcNow)));
    }
}
