using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Event rewrite fixtures

public record RewriteNoted(string Text);

public record RewriteRemarked(string Text);

#endregion

/// <summary>
/// The stored-event rewrite seam, <see cref="IEventStore.RewriteStoredEventsAsync" /> (jasperfx#1215): the part a
/// store implements for the Wolverine job that runs a rewrite.
/// </summary>
/// <remarks>
/// <para>
/// The transform is deliberately not idempotent: it appends a <c>!</c>. A row rewritten twice ends with two, so a
/// cursor written outside the page's transaction, a resume that starts before the cursor, or a run that reads past
/// its ceiling all show up as a wrong count.
/// </para>
/// <para>
/// Every fact names its own rewrite, because the marks a rewrite stores outlive data cleaning.
/// </para>
/// </remarks>
[RequiresCapability("SupportsEventRewrites")]
public abstract class EventRewriteCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    /// <summary>The non-idempotent transform every fact uses.</summary>
    public const string AppendBang = "append-bang";

    private const string TenantA = "rewrite-tenant-a";
    private const string TenantB = "rewrite-tenant-b";

    private static void configureCore(ComplianceStoreConfig config)
    {
        config.AddEventType<RewriteNoted>();
        config.AddEventType<RewriteRemarked>();
        config.AddRewriteTransform<RewriteNoted>(AppendBang, x => x with { Text = x.Text + "!" });
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_event_rewrites";
        configureCore(config);
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_event_rewrites_conjoined";
        config.ConjoinedEventTenancy = true;
        configureCore(config);
    };

    // The transform goes through a registrar member with a throwing default, so a store that does not support
    // rewrites never reaches it: its facts skip instead of failing to configure
    private static readonly Action<ComplianceStoreConfig> _unsupportedConfiguration = config =>
    {
        config.SchemaName = "compliance_event_rewrites";
    };

    protected override Action<ComplianceStoreConfig> Configuration
        => theFixture.SupportsEventRewrites ? _configuration : _unsupportedConfiguration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsEventRewrites,
            "This event store does not implement the stored-event rewrite seam (jasperfx#1215)");
        EventStore.SupportsEventRewrites.ShouldBeTrue();
    }

    private async Task<IEventDatabase> theDatabaseAsync() => (await EventStore.AllDatabases()).First();

    private static EventRewriteDefinition aRewrite(int batchSize = 500) => new()
    {
        Name = "rewrite" + Guid.NewGuid().ToString("N")[..12],
        Transforms = [AppendBang],
        BatchSize = batchSize
    };

    private async Task<Guid> aStreamAsync(params object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream(id, events);
        await SaveChangesAsync(session);
        return id;
    }

    private async Task<Guid> aStreamForTenantAsync(string tenantId, params object[] events)
    {
        var store = (IEventStore<TOperations, TQuerySession>)EventStore;
        var id = Guid.NewGuid();
        await using var session = store.OpenSession(await theDatabaseAsync(), tenantId);
        EventsFor(session).StartStream(id, events);
        await SaveChangesAsync(session);
        return id;
    }

    private async Task<IReadOnlyList<IEvent>> streamAsync(Guid id, string? tenantId = null)
    {
        if (tenantId == null)
        {
            await using var session = OpenSession();
            return await EventsFor(session).FetchStreamAsync(id, token: Cancellation);
        }

        var store = (IEventStore<TOperations, TQuerySession>)EventStore;
        await using var tenantSession = store.OpenSession(await theDatabaseAsync(), tenantId);
        return await EventsFor(tenantSession).FetchStreamAsync(id, token: Cancellation);
    }

    private static object[] notes(int count, string prefix = "note")
        => Enumerable.Range(1, count).Select(i => (object)new RewriteNoted($"{prefix}-{i}")).ToArray();

    private static int bangs(IEvent e) => e.Data switch
    {
        RewriteNoted n => n.Text.Count(c => c == '!'),
        RewriteRemarked r => r.Text.Count(c => c == '!'),
        _ => 0
    };

    private async Task everyNoteHasBangsAsync(Guid stream, int expected, string? tenantId = null)
    {
        foreach (var e in await streamAsync(stream, tenantId))
        {
            if (e.Data is RewriteNoted)
            {
                bangs(e).ShouldBe(expected, $"event {e.Sequence} of stream {stream}");
            }
        }
    }

    private async Task<IReadOnlyList<EventRewriteProgress>> rewriteAsync(EventRewriteDefinition definition,
        Func<EventRewriteProgress, CancellationToken, Task>? onPage = null, CancellationToken? token = null)
    {
        var run = await EventRewriteRun.FromMarksAsync(await theDatabaseAsync(), definition, Cancellation);
        run.OnPage = onPage;
        return await EventStore.RewriteStoredEventsAsync(run, token ?? Cancellation);
    }

    [Fact]
    public async Task an_unknown_transform_refuses_the_whole_request_before_anything_changes()
    {
        SkipUnlessSupported();
        var stream = await aStreamAsync(notes(3));

        var definition = aRewrite();
        definition.Transforms = [AppendBang, "no-such-transform"];

        var refused = await Should.ThrowAsync<UnknownRewriteTransformException>(() => rewriteAsync(definition));

        refused.UnknownNames.ShouldBe(new[] { "no-such-transform" });
        await everyNoteHasBangsAsync(stream, 0);
    }

    [Fact]
    public async Task every_selected_event_goes_through_the_transform_exactly_once()
    {
        SkipUnlessSupported();
        var stream = await aStreamAsync([..notes(5), new RewriteRemarked("left alone")]);

        var totals = await rewriteAsync(aRewrite(batchSize: 2));

        await everyNoteHasBangsAsync(stream, 1);
        (await streamAsync(stream)).Single(x => x.Data is RewriteRemarked).Data
            .ShouldBe(new RewriteRemarked("left alone"));

        var total = totals.ShouldHaveSingleItem();
        total.TenantId.ShouldBeNull();
        total.Completed.ShouldBeTrue();
        total.Visited.ShouldBe(5);
        total.Rewritten.ShouldBe(5);
    }

    [Fact]
    public async Task each_page_is_reported_with_the_cursor_stored_with_it()
    {
        SkipUnlessSupported();
        await aStreamAsync(notes(5));
        var definition = aRewrite(batchSize: 2);
        var database = await theDatabaseAsync();
        var cursorKey = EventRangeMarks.CursorKey(EventRangeMarks.RewriteKey(definition.Name));

        var pages = new List<(EventRewriteProgress Page, long? Stored)>();
        await rewriteAsync(definition, async (page, token) =>
        {
            pages.Add((page, await database.FetchEventRangeMarkAsync(cursorKey, null, token)));
        });

        pages.Count.ShouldBeGreaterThanOrEqualTo(3);
        foreach (var (page, stored) in pages)
        {
            stored.ShouldBe(page.Sequence);
        }

        pages.Select(x => x.Page.Sequence).ShouldBeInOrder();
        pages[^1].Page.Completed.ShouldBeTrue();
        pages.Take(pages.Count - 1).ShouldAllBe(x => !x.Page.Completed);
    }

    [Fact]
    public async Task a_stopped_rewrite_resumes_after_its_cursor_without_rewriting_an_event_twice()
    {
        SkipUnlessSupported();
        var stream = await aStreamAsync(notes(7));
        var definition = aRewrite(batchSize: 2);

        // Stop after the first page has committed
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        EventRewriteProgress? first = null;
        await Should.ThrowAsync<OperationCanceledException>(() => rewriteAsync(definition, (page, _) =>
        {
            first ??= page;
            stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token));

        first.ShouldNotBeNull().Completed.ShouldBeFalse();
        var database = await theDatabaseAsync();
        (await database.FetchEventRangeMarkAsync(
                EventRangeMarks.CursorKey(EventRangeMarks.RewriteKey(definition.Name)), null, Cancellation))
            .ShouldBe(first.Sequence);

        // A new run from the marks picks up after the stored cursor
        var resumed = await rewriteAsync(definition);

        resumed.ShouldHaveSingleItem().Completed.ShouldBeTrue();
        await everyNoteHasBangsAsync(stream, 1);
    }

    [Fact]
    public async Task events_appended_after_the_ceiling_are_left_alone()
    {
        SkipUnlessSupported();
        var before = await aStreamAsync(notes(3));
        var definition = aRewrite();

        // The ceiling is captured when the run is first requested
        var database = await theDatabaseAsync();
        var run = await EventRewriteRun.FromMarksAsync(database, definition, Cancellation);
        var ceiling = run.Cursors.ShouldHaveSingleItem().StopAfter;

        var after = await aStreamAsync(notes(3, "late"));

        await EventStore.RewriteStoredEventsAsync(run, Cancellation);

        await everyNoteHasBangsAsync(before, 1);
        await everyNoteHasBangsAsync(after, 0);
        (await database.FetchEventRangeMarkAsync(run.CursorKey, null, Cancellation)).ShouldBe(ceiling);
    }

    [Fact]
    public async Task a_completed_rewrite_does_nothing_when_run_again()
    {
        SkipUnlessSupported();
        var stream = await aStreamAsync(notes(4));
        var definition = aRewrite();

        await rewriteAsync(definition);
        var again = await rewriteAsync(definition);

        var total = again.ShouldHaveSingleItem();
        total.Visited.ShouldBe(0);
        total.Rewritten.ShouldBe(0);
        total.Completed.ShouldBeTrue();
        await everyNoteHasBangsAsync(stream, 1);
    }

    [Fact]
    public async Task a_dry_run_counts_but_writes_nothing()
    {
        SkipUnlessSupported();
        var stream = await aStreamAsync(notes(4));
        var definition = aRewrite();
        definition.DryRun = true;

        var totals = await rewriteAsync(definition);

        totals.ShouldHaveSingleItem().Rewritten.ShouldBe(4);
        await everyNoteHasBangsAsync(stream, 0);

        var database = await theDatabaseAsync();
        (await database.FetchEventRangeMarkAsync(
                EventRangeMarks.CursorKey(EventRangeMarks.RewriteKey(definition.Name)), null, Cancellation))
            .ShouldBeNull();
    }

    [Fact]
    public async Task tenants_are_rewritten_one_after_another_each_with_its_own_cursor()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy");
        await theFixture.ConfigureAsync(_conjoinedConfiguration);
        await theFixture.CleanEventDataAsync();

        var a = await aStreamForTenantAsync(TenantA, notes(3, "a"));
        var b = await aStreamForTenantAsync(TenantB, notes(3, "b"));
        var untouched = await aStreamForTenantAsync("rewrite-tenant-c", notes(2, "c"));

        var definition = aRewrite(batchSize: 2);
        definition.Tenants = [TenantA, TenantB];

        var totals = await rewriteAsync(definition);

        totals.Select(x => x.TenantId).ShouldBe(new string?[] { TenantA, TenantB });
        totals.ShouldAllBe(x => x.Completed);

        await everyNoteHasBangsAsync(a, 1, TenantA);
        await everyNoteHasBangsAsync(b, 1, TenantB);
        await everyNoteHasBangsAsync(untouched, 0, "rewrite-tenant-c");

        var database = await theDatabaseAsync();
        var cursorKey = EventRangeMarks.CursorKey(EventRangeMarks.RewriteKey(definition.Name));
        foreach (var total in totals)
        {
            (await database.FetchEventRangeMarkAsync(cursorKey, total.TenantId, Cancellation))
                .ShouldBe(total.Sequence);
        }
    }
}
