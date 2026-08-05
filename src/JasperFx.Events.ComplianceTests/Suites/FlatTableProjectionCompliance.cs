using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Flat table events

public record FlatValuesSet(int A, int B, int C, int D, int MemberCount);

public record FlatValuesAdded(int A, int B, int C, int D);

public record FlatValuesSubtracted(int A, int B, int C, int D);

public record FlatValuesDeleted;

#endregion

/// <summary>
/// The flat-table projection under compliance: one table keyed on the stream id, written by three
/// mapping event types and emptied by a fourth.
/// </summary>
/// <remarks>
/// <para>
/// This is the first shared type in the library whose base class cannot be reached by an alias
/// alone. Every product's flat-table projection base takes constructor arguments describing where
/// the table lives, and those signatures genuinely differ (Marten resolves the schema through a
/// <c>SchemaNameSource</c> enum; Polecat takes a literal schema name), so no single
/// <c>base(...)</c> call satisfies both. Declaring the primary key is likewise per-product, because
/// the column-declaration API hangs off each dialect's own <c>Table</c> type.
/// </para>
/// <para>
/// So the shape is split: everything portable — the table name, the projection name, and every
/// event mapping — lives here, and each consumer supplies a small partial carrying the constructor
/// and the primary key column. That shim is the honest cost of this suite, and it is deliberately
/// visible rather than hidden behind more seam surface. Lifting the flat-table projection shape
/// onto a shared base in JasperFx.Events would retire it (see marten#5147), but doing that with
/// only two dialects in hand risks designing the abstraction around exactly those two.
/// </para>
/// <para>
/// The library stays free of any Weasel reference on purpose. <c>Weasel.Core.ITable</c> does now
/// offer a dialect-neutral column surface — <c>AddColumn(name, Type)</c> and
/// <c>AddPrimaryKeyColumn(name, Type)</c>, routed through a per-dialect type resolver — which would
/// let the primary key move into this file. It is not used here because these sources compile
/// inside every consumer, so a Weasel dependency in one suite becomes a Weasel dependency for the
/// whole package, and a JasperFx.Events store is not obliged to be built on Weasel.
/// </para>
/// </remarks>
public partial class ComplianceFlatTableProjection
{
    /// <summary>
    /// Unqualified name of the projected table. Consumers pass this to their base constructor and
    /// suites hand it to <c>QueryTableAsync</c>.
    /// </summary>
    public const string TableName = "compliance_flat_values";

    /// <summary>
    /// Schema the suite's stores are built in. A consumer whose flat-table base takes a literal
    /// schema name needs this; one that resolves the store's schema at runtime can ignore it.
    /// </summary>
    /// <remarks>
    /// Lower case because at least one consumer normalizes schema names to lower case when building
    /// the store, and the two have to agree or the projection writes somewhere the suite is not
    /// looking.
    /// </remarks>
    public const string SchemaName = "compliance_flat_table";

    /// <summary>
    /// The daemon-facing projection name, pinned rather than defaulted.
    /// </summary>
    /// <remarks>
    /// Products disagree on the default: one uses the projection's short type name, another its
    /// full name. Rebuild is addressed by name, so the suite sets it explicitly instead of encoding
    /// either default.
    /// </remarks>
    public const string ProjectionName = "ComplianceFlatTable";

    /// <summary>
    /// Every portable part of the projection definition. A consumer's constructor declares the
    /// primary key column and then calls this.
    /// </summary>
    protected void ConfigureMappings()
    {
        Name = ProjectionName;

        Project<FlatValuesSet>(map =>
        {
            // No explicit column names: deriving the column from the event member is part of the
            // contract, and both single-word (A) and multi-word (MemberCount) members are covered.
            map.Map(x => x.A);
            map.Map(x => x.B);
            map.Map(x => x.C);
            map.Map(x => x.D);
            map.Map(x => x.MemberCount);

            map.SetValue("status", "new");
            map.SetValue("revision", 1);
        });

        Project<FlatValuesAdded>(map =>
        {
            map.Increment(x => x.A);
            map.Increment(x => x.B);
            map.Increment(x => x.C);
            map.Increment(x => x.D);

            map.Increment("revision");

            map.SetValue("status", "old");
        });

        Project<FlatValuesSubtracted>(map =>
        {
            map.Decrement(x => x.A);
            map.Decrement(x => x.B);
            map.Decrement(x => x.C);
            map.Decrement(x => x.D);

            map.Decrement("revision");

            map.SetValue("status", "old");
        });

        Delete<FlatValuesDeleted>();
    }
}

/// <summary>
/// Flat-table event projections — an <c>EventProjection</c> that writes into a plain relational
/// table keyed on the stream id, rather than into a document.
/// </summary>
/// <remarks>
/// <para>
/// Worth pinning across stores because the implementations underneath are genuinely different
/// mechanisms for the same promised semantics: one product generates per-event upsert functions,
/// another emits <c>MERGE</c> statements. Both claim insert-or-update, increment, decrement and
/// delete; whether they agree at the edges — a second event updating rather than duplicating a row,
/// an increment landing on a row that does not exist yet, a rebuild replaying increments exactly
/// once — is exactly what two independent implementations get wrong in different ways.
/// </para>
/// <para>
/// Table *shape* is asserted only through the columns that come back with a row. The generated DDL,
/// the upsert mechanism, index layout and identifier quoting are all storage layout and stay in
/// each product's own tests.
/// </para>
/// </remarks>
public abstract class FlatTableProjectionCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = ComplianceFlatTableProjection.SchemaName;

        config.AddEventType<FlatValuesSet>();
        config.AddEventType<FlatValuesAdded>();
        config.AddEventType<FlatValuesSubtracted>();
        config.AddEventType<FlatValuesDeleted>();

        config.AddProjection(new ComplianceFlatTableProjection(), ProjectionLifecycle.Inline);
    };

    /// <summary>
    /// The same projection registered <see cref="ProjectionLifecycle.Async"/>, in the same schema so
    /// the table is the one the inline tests already proved out.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = ComplianceFlatTableProjection.SchemaName;

        config.AddEventType<FlatValuesSet>();
        config.AddEventType<FlatValuesAdded>();
        config.AddEventType<FlatValuesSubtracted>();
        config.AddEventType<FlatValuesDeleted>();

        config.AddProjection(new ComplianceFlatTableProjection(), ProjectionLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Capability gate. xunit v3's declarative SkipUnless needs a *static* property, which cannot
    /// consult a per-store fixture instance, so the gate runs as a dynamic skip instead.
    /// </summary>
    private void SkipUnlessFlatTablesAreSupported()
    {
        Assert.SkipUnless(theFixture.SupportsFlatTableProjections,
            "This event store does not implement flat table event projections");
    }

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    private async Task appendAsync(Guid streamId, params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(streamId, events);
        await SaveChangesAsync(session);
    }

    /// <summary>
    /// The single row this test appended under, or null when the projection deleted it.
    /// </summary>
    /// <remarks>
    /// Filtering happens here, in memory, rather than in the query: see
    /// <c>QueryTableAsync</c> for why that member stays predicate-free. Tests use a fresh stream id
    /// so rows left by sibling tests in the same table are simply not theirs.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, object?>?> rowAsync(Guid streamId)
    {
        var rows = await QueryTableAsync(ComplianceFlatTableProjection.TableName);

        var mine = rows.Where(x => Equals(x["id"], streamId)).ToList();

        mine.Count.ShouldBeLessThanOrEqualTo(1,
            $"The flat table holds {mine.Count} rows for stream {streamId}; the projection should upsert a single row per identity");

        return mine.SingleOrDefault();
    }

    private static int intValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        var raw = row[column];
        raw.ShouldNotBeNull($"Column '{column}' came back null");

        // Stores are free to widen a numeric column (int vs bigint vs numeric); the contract is the
        // value, not the width it was stored at.
        return Convert.ToInt32(raw);
    }

    [Fact]
    public async Task the_projected_table_carries_the_mapped_columns()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId, new FlatValuesSet(1, 2, 3, 4, 5));

        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        // The primary key plus every column the mappings named or derived.
        foreach (var column in new[] { "id", "a", "b", "c", "d", "member_count", "status", "revision" })
        {
            row.ContainsKey(column).ShouldBeTrue(
                $"Expected the flat table to have a '{column}' column, but it has: {string.Join(", ", row.Keys)}");
        }
    }

    [Fact]
    public async Task mapping_an_event_writes_a_new_row()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId, new FlatValuesSet(3, 4, 5, 6, 7));

        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        intValue(row, "a").ShouldBe(3);
        intValue(row, "b").ShouldBe(4);
        intValue(row, "c").ShouldBe(5);
        intValue(row, "d").ShouldBe(6);

        row["status"].ShouldBe("new");
        intValue(row, "revision").ShouldBe(1);
    }

    [Fact]
    public async Task column_names_are_derived_from_the_event_members()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId, new FlatValuesSet(1, 2, 3, 4, 11));

        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        // A single-word member maps to its lower-cased name, a multi-word member to snake case.
        intValue(row, "a").ShouldBe(1);
        intValue(row, "member_count").ShouldBe(11);
    }

    [Fact]
    public async Task a_later_event_updates_the_row_rather_than_inserting_a_second_one()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId, new FlatValuesSet(1, 2, 3, 4, 5));
        await appendAsync(streamId, new FlatValuesAdded(10, 10, 10, 10));

        // rowAsync fails if the store inserted a second row for the same identity.
        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        intValue(row, "a").ShouldBe(11);
        intValue(row, "b").ShouldBe(12);
        intValue(row, "c").ShouldBe(13);
        intValue(row, "d").ShouldBe(14);

        row["status"].ShouldBe("old");
        intValue(row, "revision").ShouldBe(2);
    }

    [Fact]
    public async Task increment_and_decrement_move_the_column_in_both_directions()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId,
            new FlatValuesSet(10, 20, 30, 40, 1),
            new FlatValuesAdded(5, 5, 5, 5),
            new FlatValuesSubtracted(2, 2, 2, 2));

        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        intValue(row, "a").ShouldBe(13);
        intValue(row, "b").ShouldBe(23);
        intValue(row, "c").ShouldBe(33);
        intValue(row, "d").ShouldBe(43);

        // Set to 1, incremented once, decremented once.
        intValue(row, "revision").ShouldBe(1);
    }

    [Fact]
    public async Task a_delete_event_removes_the_row()
    {
        SkipUnlessFlatTablesAreSupported();

        var streamId = Guid.NewGuid();
        await appendAsync(streamId, new FlatValuesSet(1, 2, 3, 4, 5));

        (await rowAsync(streamId)).ShouldNotBeNull();

        await appendAsync(streamId, new FlatValuesDeleted());

        (await rowAsync(streamId)).ShouldBeNull();
    }

    [Fact]
    public async Task an_async_projection_produces_the_same_row_as_an_inline_one()
    {
        SkipUnlessFlatTablesAreSupported();
        SkipUnlessDaemonIsSupported();

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var streamId = Guid.NewGuid();
        await appendAsync(streamId,
            new FlatValuesSet(3, 4, 5, 6, 7),
            new FlatValuesAdded(1, 1, 1, 1));

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var row = await rowAsync(streamId);
        row.ShouldNotBeNull();

        // Byte for byte what the inline lifecycle produces from the same two events.
        intValue(row, "a").ShouldBe(4);
        intValue(row, "b").ShouldBe(5);
        intValue(row, "c").ShouldBe(6);
        intValue(row, "d").ShouldBe(7);

        row["status"].ShouldBe("old");
        intValue(row, "revision").ShouldBe(2);
    }

    [Fact]
    public async Task a_rebuild_empties_the_table_before_replaying_it()
    {
        SkipUnlessFlatTablesAreSupported();
        SkipUnlessDaemonIsSupported();

        await theFixture.ConfigureAsync(_asyncConfiguration);

        // Two rows: one whose events the rebuild will still see, and one whose events it will not.
        // The second is what makes this test discriminating at all. Because Map overwrites rather
        // than accumulates, a rebuild that replayed straight onto surviving rows would land on
        // exactly the same values as one that emptied the table first, so the only observable
        // difference is whether a row the replay cannot recreate is still there afterwards.
        var archived = Guid.NewGuid();
        await appendAsync(archived, new FlatValuesSet(9, 9, 9, 9, 9));

        var replayed = Guid.NewGuid();
        await appendAsync(replayed,
            new FlatValuesSet(1, 1, 1, 1, 1),
            new FlatValuesAdded(2, 2, 2, 2));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await rowAsync(archived)).ShouldNotBeNull();

        // Archiving is how the suite takes events away from the replay without touching the table
        // directly. Deleting event data is not usable here: at least one store's cleaning also
        // erases flat-table rows, which would make this assertion pass for the wrong reason.
        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(archived);
            await SaveChangesAsync(session);
        }

        await daemon.RebuildProjectionAsync(ComplianceFlatTableProjection.ProjectionName, Cancellation);

        (await rowAsync(archived)).ShouldBeNull(
            "A rebuild should start the flat table from empty, so a row the replay cannot recreate should not survive it");

        var after = await rowAsync(replayed);
        after.ShouldNotBeNull();

        // And the replay itself applies each event exactly once.
        intValue(after, "a").ShouldBe(3);
        intValue(after, "revision").ShouldBe(2);
    }
}
