using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Backfill fixtures

public record BackfillLedgerOpened;

public record BackfillLineAdded(int Amount);

/// <summary>An event the backfill does not read.</summary>
public record BackfillNoted(string Text);

/// <summary>
/// The member <see cref="ComplianceBackfillProjection" /> adds in its second version, and the backfill fills on the
/// documents its first version wrote.
/// </summary>
public partial class ComplianceWatchtower
{
    public int LineTotal { get; set; }

    /// <summary>The sequences the live version applied, so a fact can see which side handled which event.</summary>
    public List<long> LiveLineSequences { get; set; } = new();
}

/// <summary>
/// Version 2 of a single-stream projection that starts where version 1 left off
/// (<see cref="AsyncOptions.SubscribeFromPriorVersionMark" />), while the backfill named <see cref="BackfillName" />
/// fills <see cref="ComplianceWatchtower.LineTotal" /> on the documents version 1 wrote.
/// </summary>
/// <remarks>
/// <para>
/// The store declares the backfill with its own syntax, so a consumer implements the partial method
/// <c>declareBackfill()</c> in its compliance assembly, adding <see cref="BackfillLineAdded.Amount" /> to
/// <see cref="ComplianceWatchtower.LineTotal" />:
/// </para>
/// <code>
/// public partial class ComplianceBackfillProjection
/// {
///     partial void declareBackfill()
///         =&gt; Backfill(BackfillName).On&lt;BackfillLineAdded&gt;((doc, e) =&gt; doc.LineTotal += e.Amount);
/// }
/// </code>
/// <para>
/// The amounts in the facts are powers of ten, so a total that is wrong in any digit names the event that was lost
/// or applied twice.
/// </para>
/// </remarks>
public partial class ComplianceBackfillProjection: ComplianceWatchtowerProjectionBase
{
    public const string ProjectionName = "ComplianceBackfill";
    public const string BackfillName = "line-total";

    public ComplianceBackfillProjection()
    {
        Name = ProjectionName;
        Version = 2;
        Options.SubscribeFromPriorVersionMark(BackfillName);

        declareBackfill();
    }

    partial void declareBackfill();

    public void Apply(BackfillLedgerOpened _, ComplianceWatchtower tower)
    {
    }

    public void Apply(IEvent<BackfillLineAdded> @event, ComplianceWatchtower tower)
    {
        tower.LineTotal += @event.Data.Amount;
        tower.LiveLineSequences.Add(@event.Sequence);
    }
}

#endregion

/// <summary>
/// The projection backfill seam, <see cref="IEventStore.RunBackfillAsync" />, and the live start it splits history
/// with, <see cref="AsyncOptions.SubscribeFromPriorVersionMark" /> (jasperfx#1222).
/// </summary>
/// <remarks>
/// <para>
/// A fact stands in for version 1 of the projection by storing its documents directly and seeding its progression
/// row, which is what a deployment of version 2 finds.
/// </para>
/// <para>
/// The fixture builds the store with <see cref="ComplianceStoreConfig.HasJobHost" /> set, since a backfill only runs
/// as a Wolverine job. The race between the backfill's writes and the live version's writes to the same document
/// belongs to each store's own tests; the facts here let the live version catch up before the backfill runs.
/// </para>
/// </remarks>
[RequiresCapability("SupportsBackfills")]
public abstract class BackfillCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private const string TenantA = "backfill-tenant-a";
    private const string TenantB = "backfill-tenant-b";

    private static void configureCore(ComplianceStoreConfig config)
    {
        config.HasJobHost = true;
        config.AddEventType<BackfillLedgerOpened>();
        config.AddEventType<BackfillLineAdded>();
        config.AddEventType<BackfillNoted>();
        config.AddProjection(new ComplianceBackfillProjection(), ProjectionLifecycle.Async);
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_backfill";
        configureCore(config);
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_backfill_conjoined";
        config.ConjoinedEventTenancy = true;
        configureCore(config);
    };

    private static readonly Action<ComplianceStoreConfig> _withoutJobHostConfiguration = config =>
    {
        config.SchemaName = "compliance_backfill";
        configureCore(config);
        config.HasJobHost = false;
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsBackfills,
            "This event store does not implement the projection backfill seam (jasperfx#1222)");
        EventStore.SupportsBackfills.ShouldBeTrue();
    }

    private IEventStore<TOperations, TQuerySession> Store => (IEventStore<TOperations, TQuerySession>)EventStore;

    private async Task<IEventDatabase> theDatabaseAsync() => (await EventStore.AllDatabases()).First();

    private ShardName theLiveShard => EventStore.RegisteredShardNames()
        .Single(x => x.Name == ComplianceBackfillProjection.ProjectionName);

    private static readonly ShardName thePriorShard =
        new(ComplianceBackfillProjection.ProjectionName, ShardName.All, 1);

    private static string theCeilingKey =>
        EventRangeMarks.BackfillKey(ComplianceBackfillProjection.ProjectionName, ComplianceBackfillProjection.BackfillName);

    private static string theCursorKey => EventRangeMarks.CursorKey(theCeilingKey);

    private async Task<Guid> aLedgerAsync(params object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceWatchtower>(id, [new BackfillLedgerOpened(), ..events]);
        await SaveChangesAsync(session);
        return id;
    }

    private async Task appendAsync(Guid id, params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(id, events);
        await SaveChangesAsync(session);
    }

    private static object[] lines(params int[] amounts) => amounts.Select(x => (object)new BackfillLineAdded(x)).ToArray();

    // What version 1 of the projection wrote: the document without the new member
    private async Task asVersionOneWroteAsync(params Guid[] ids)
    {
        await using var session = OpenSession();
        foreach (var id in ids)
        {
            StoreDocument(session, new ComplianceWatchtower { Id = id, Name = "version 1" });
        }

        await SaveChangesAsync(session);
    }

    // Version 1's progression row, at the head of the store
    private async Task<long> versionOneReachedTheHeadAsync()
    {
        var database = await theDatabaseAsync();
        var head = await database.FetchHighestEventSequenceNumber(Cancellation);
        await Store.RewindSubscriptionProgressAsync(database, thePriorShard.Identity, Cancellation, head);
        return head;
    }

    private async Task<ComplianceWatchtower> loadAsync(Guid id)
    {
        await using var session = OpenSession();
        return (await LoadDocumentAsync<ComplianceWatchtower>(session, id)).ShouldNotBeNull($"no document for {id}");
    }

    private async Task<IReadOnlyList<BackfillProgress>> backfillAsync(
        Func<BackfillProgress, CancellationToken, Task>? onPage = null, int batchSize = 500,
        IReadOnlyList<string?>? tenants = null, CancellationToken? token = null)
    {
        var run = await BackfillRun.FromMarksAsync(await theDatabaseAsync(), theLiveShard,
            ComplianceBackfillProjection.BackfillName, tenants, Cancellation);
        run.BatchSize = batchSize;
        run.OnPage = onPage;
        return await EventStore.RunBackfillAsync(run, token ?? Cancellation);
    }

    [Fact]
    public async Task the_live_version_and_the_backfill_split_history_at_exactly_one_sequence()
    {
        SkipUnlessSupported();

        var first = await aLedgerAsync(lines(1, 10));
        var second = await aLedgerAsync([..lines(100), new BackfillNoted("not read")]);
        await asVersionOneWroteAsync(first, second);
        var mark = await versionOneReachedTheHeadAsync();

        // Version 2 starts at the prior version's mark and handles everything after it
        await StartDaemonAsync();
        await appendAsync(first, lines(1_000));
        await appendAsync(second, lines(10_000));
        var third = await aLedgerAsync(lines(100_000));
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var database = await theDatabaseAsync();
        (await database.FetchEventRangeMarkAsync(theCeilingKey, theLiveShard.TenantId, Cancellation)).ShouldBe(mark);
        foreach (var id in new[] { first, second, third })
        {
            (await loadAsync(id)).LiveLineSequences.ShouldAllBe(x => x > mark);
        }

        // The backfill covers everything up to and including the same mark
        var totals = await backfillAsync();

        (await loadAsync(first)).LineTotal.ShouldBe(1_011);
        (await loadAsync(second)).LineTotal.ShouldBe(10_100);
        (await loadAsync(third)).LineTotal.ShouldBe(100_000);

        var total = totals.ShouldHaveSingleItem();
        total.Completed.ShouldBeTrue();
        total.Sequence.ShouldBe(mark);
        total.Visited.ShouldBe(3);
        total.Applied.ShouldBe(3);
        (await database.FetchEventRangeMarkAsync(theCursorKey, theLiveShard.TenantId, Cancellation)).ShouldBe(mark);
    }

    [Fact]
    public async Task whichever_side_captures_the_mark_first_the_other_uses_it()
    {
        SkipUnlessSupported();

        var ledger = await aLedgerAsync(lines(1, 10));
        await asVersionOneWroteAsync(ledger);
        var mark = await versionOneReachedTheHeadAsync();

        // The backfill job captures the ceiling first...
        var run = await BackfillRun.FromMarksAsync(await theDatabaseAsync(), theLiveShard,
            ComplianceBackfillProjection.BackfillName, null, Cancellation);
        run.Cursors.ShouldHaveSingleItem().StopAfter.ShouldBe(mark);

        // ...then version 1 keeps running on a node that has not been upgraded yet
        await appendAsync(ledger, lines(100));
        await versionOneReachedTheHeadAsync();

        // Version 2 still starts at the captured mark, so it handles the event version 1 went on to process
        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await EventStore.RunBackfillAsync(run, Cancellation);

        var document = await loadAsync(ledger);
        document.LineTotal.ShouldBe(111);
        document.LiveLineSequences.ShouldHaveSingleItem().ShouldBeGreaterThan(mark);
    }

    [Fact]
    public async Task a_stopped_backfill_resumes_after_its_cursor_without_applying_an_event_twice()
    {
        SkipUnlessSupported();

        var first = await aLedgerAsync(lines(1, 10, 100));
        var second = await aLedgerAsync(lines(1_000, 10_000, 100_000));
        await asVersionOneWroteAsync(first, second);
        await versionOneReachedTheHeadAsync();

        // Stop after the first page has committed
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        BackfillProgress? firstPage = null;
        await Should.ThrowAsync<OperationCanceledException>(() => backfillAsync((page, _) =>
        {
            firstPage ??= page;
            stop.Cancel();
            return Task.CompletedTask;
        }, batchSize: 2, token: stop.Token));

        firstPage.ShouldNotBeNull().Completed.ShouldBeFalse();
        var database = await theDatabaseAsync();
        (await database.FetchEventRangeMarkAsync(theCursorKey, theLiveShard.TenantId, Cancellation))
            .ShouldBe(firstPage.Sequence);

        // A new run from the marks picks up after the stored cursor
        var resumed = await backfillAsync(batchSize: 2);

        resumed.ShouldHaveSingleItem().Completed.ShouldBeTrue();
        (await loadAsync(first)).LineTotal.ShouldBe(111);
        (await loadAsync(second)).LineTotal.ShouldBe(111_000);
    }

    [Fact]
    public async Task events_after_the_ceiling_are_left_to_the_live_version()
    {
        SkipUnlessSupported();

        var ledger = await aLedgerAsync(lines(1, 10));
        await asVersionOneWroteAsync(ledger);
        var mark = await versionOneReachedTheHeadAsync();

        var run = await BackfillRun.FromMarksAsync(await theDatabaseAsync(), theLiveShard,
            ComplianceBackfillProjection.BackfillName, null, Cancellation);

        // No daemon: nothing handles these, and the backfill must not either
        await appendAsync(ledger, lines(100, 1_000));

        var totals = await EventStore.RunBackfillAsync(run, Cancellation);

        (await loadAsync(ledger)).LineTotal.ShouldBe(11);
        totals.ShouldHaveSingleItem().Sequence.ShouldBe(mark);
    }

    [Fact]
    public async Task a_completed_backfill_does_nothing_when_run_again()
    {
        SkipUnlessSupported();

        var ledger = await aLedgerAsync(lines(1, 10));
        await asVersionOneWroteAsync(ledger);
        await versionOneReachedTheHeadAsync();

        await backfillAsync();
        var again = await backfillAsync();

        var total = again.ShouldHaveSingleItem();
        total.Completed.ShouldBeTrue();
        total.Visited.ShouldBe(0);
        (await loadAsync(ledger)).LineTotal.ShouldBe(11);
    }

    [Fact]
    public async Task the_marks_survive_a_rebuild_of_the_projection()
    {
        SkipUnlessSupported();

        var ledger = await aLedgerAsync(lines(1, 10));
        await asVersionOneWroteAsync(ledger);
        var mark = await versionOneReachedTheHeadAsync();
        await backfillAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await daemon.RebuildProjectionAsync(ComplianceBackfillProjection.ProjectionName, _timeout, Cancellation);

        var database = await theDatabaseAsync();
        (await database.FetchEventRangeMarkAsync(theCeilingKey, theLiveShard.TenantId, Cancellation)).ShouldBe(mark);
        (await database.FetchEventRangeMarkAsync(theCursorKey, theLiveShard.TenantId, Cancellation)).ShouldBe(mark);
    }

    [Fact]
    public async Task each_tenant_runs_to_its_own_ceiling()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy");
        await theFixture.ConfigureAsync(_conjoinedConfiguration);
        await theFixture.CleanEventDataAsync();

        var database = await theDatabaseAsync();

        async Task aTenantLedgerAsync(string tenantId, params int[] amounts)
        {
            await using var session = Store.OpenSession(database, tenantId);
            EventsFor(session).StartStream<ComplianceWatchtower>(Guid.NewGuid(),
                [new BackfillLedgerOpened(), ..lines(amounts)]);
            await SaveChangesAsync(session);
        }

        // Tenant A's ceiling is captured after two of its lines, tenant B's after all three of its own
        await aTenantLedgerAsync(TenantA, 1, 10);
        var ceilingA = await database.FetchHighestEventSequenceNumber(Cancellation);
        await database.TrySetEventRangeMarkAsync(theCeilingKey, TenantA, ceilingA, Cancellation);

        await aTenantLedgerAsync(TenantA, 100);
        await aTenantLedgerAsync(TenantB, 1, 10, 100);
        var ceilingB = await database.FetchHighestEventSequenceNumber(Cancellation);
        await database.TrySetEventRangeMarkAsync(theCeilingKey, TenantB, ceilingB, Cancellation);

        await aTenantLedgerAsync(TenantB, 1_000);

        var totals = await backfillAsync(tenants: [TenantA, TenantB], batchSize: 2);

        totals.Select(x => x.TenantId).ShouldBe(new string?[] { TenantA, TenantB });
        totals.ShouldAllBe(x => x.Completed);
        totals[0].Visited.ShouldBe(2);
        totals[1].Visited.ShouldBe(3);

        (await database.FetchEventRangeMarkAsync(theCursorKey, TenantA, Cancellation)).ShouldBe(ceilingA);
        (await database.FetchEventRangeMarkAsync(theCursorKey, TenantB, Cancellation)).ShouldBe(ceilingB);
    }

    [Fact]
    public async Task a_backfill_without_a_job_host_is_refused_at_startup()
    {
        SkipUnlessSupported();

        await Should.ThrowAsync<Exception>(() => theFixture.ConfigureAsync(_withoutJobHostConfiguration));
    }
}
