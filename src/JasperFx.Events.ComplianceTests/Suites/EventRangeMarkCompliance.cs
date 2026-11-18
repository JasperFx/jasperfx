using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The event-range marks on <see cref="IEventDatabase" /> (jasperfx#1215 / #1222): where a stored-event rewrite and
/// a projection backfill keep their ceiling and their cursor.
/// </summary>
/// <remarks>
/// <para>
/// Pins the seam each store implements: absent until stored, insert-if-absent with the stored winner returned, an
/// unconditional set and clear, one mark per key and per tenant, and agreement under concurrent first requests.
/// <see cref="BackfillCompliance{TFixture,TOperations,TQuerySession}" /> covers marks outliving a rebuild.
/// </para>
/// <para>
/// Every fact uses its own key. Marks outlive data cleaning on purpose, so facts cannot share one.
/// </para>
/// </remarks>
[RequiresCapability("SupportsEventRangeMarks")]
public abstract class EventRangeMarkCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_event_range_marks";
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<IEventDatabase> DatabaseAsync()
    {
        Assert.SkipUnless(theFixture.SupportsEventRangeMarks,
            "This event store does not implement the event-range marks (jasperfx#1222).");
        return (await theFixture.EventStore.AllDatabases()).First();
    }

    private static string AKey() => EventRangeMarks.RewriteKey("mark" + Guid.NewGuid().ToString("N")[..12]);

    [Fact]
    public async Task there_is_no_mark_until_one_is_stored()
    {
        var database = await DatabaseAsync();

        (await database.FetchEventRangeMarkAsync(AKey(), null, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task the_first_try_set_stores_the_mark_and_returns_it()
    {
        var database = await DatabaseAsync();
        var key = AKey();

        (await database.TrySetEventRangeMarkAsync(key, null, 500, Cancellation)).ShouldBe(500);
        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(500);
    }

    [Fact]
    public async Task a_later_try_set_returns_the_stored_winner_and_changes_nothing()
    {
        var database = await DatabaseAsync();
        var key = AKey();
        await database.TrySetEventRangeMarkAsync(key, null, 500, Cancellation);

        (await database.TrySetEventRangeMarkAsync(key, null, 900, Cancellation)).ShouldBe(500);
        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(500);
    }

    [Fact]
    public async Task concurrent_first_requests_agree_on_one_mark()
    {
        var database = await DatabaseAsync();
        var key = AKey();

        var winners = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(i => database.TrySetEventRangeMarkAsync(key, null, i * 100, Cancellation)));

        winners.Distinct().Count().ShouldBe(1);
        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(winners[0]);
    }

    [Fact]
    public async Task a_set_overwrites_the_stored_mark()
    {
        var database = await DatabaseAsync();
        var key = AKey();
        await database.TrySetEventRangeMarkAsync(key, null, 500, Cancellation);

        // A rewind moves a cursor back
        await database.SetEventRangeMarkAsync(key, null, 42, Cancellation);

        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(42);
    }

    [Fact]
    public async Task a_set_works_without_a_prior_mark()
    {
        var database = await DatabaseAsync();
        var key = AKey();

        await database.SetEventRangeMarkAsync(key, null, 42, Cancellation);

        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(42);
    }

    [Fact]
    public async Task a_set_can_be_repeated()
    {
        // A store writes a cursor once per page
        var database = await DatabaseAsync();
        var key = EventRangeMarks.CursorKey(AKey());

        foreach (var cursor in new long[] { 10, 20, 30 })
        {
            await database.SetEventRangeMarkAsync(key, null, cursor, Cancellation);
        }

        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(30);
    }

    [Fact]
    public async Task clearing_a_mark_removes_it_so_the_next_try_set_stores_again()
    {
        var database = await DatabaseAsync();
        var key = AKey();
        await database.TrySetEventRangeMarkAsync(key, null, 500, Cancellation);

        await database.SetEventRangeMarkAsync(key, null, null, Cancellation);

        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBeNull();
        (await database.TrySetEventRangeMarkAsync(key, null, 700, Cancellation)).ShouldBe(700);
    }

    [Fact]
    public async Task a_ceiling_and_its_cursor_are_separate_marks()
    {
        var database = await DatabaseAsync();
        var ceiling = AKey();
        var cursor = EventRangeMarks.CursorKey(ceiling);

        await database.TrySetEventRangeMarkAsync(ceiling, null, 500, Cancellation);
        await database.SetEventRangeMarkAsync(cursor, null, 120, Cancellation);

        (await database.FetchEventRangeMarkAsync(ceiling, null, Cancellation)).ShouldBe(500);
        (await database.FetchEventRangeMarkAsync(cursor, null, Cancellation)).ShouldBe(120);

        await database.SetEventRangeMarkAsync(cursor, null, null, Cancellation);
        (await database.FetchEventRangeMarkAsync(ceiling, null, Cancellation)).ShouldBe(500);
    }

    [Fact]
    public async Task each_tenant_has_its_own_mark()
    {
        var database = await DatabaseAsync();
        var key = AKey();

        await database.TrySetEventRangeMarkAsync(key, "acme", 10, Cancellation);
        await database.TrySetEventRangeMarkAsync(key, "globex", 20, Cancellation);

        (await database.FetchEventRangeMarkAsync(key, "acme", Cancellation)).ShouldBe(10);
        (await database.FetchEventRangeMarkAsync(key, "globex", Cancellation)).ShouldBe(20);
        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBeNull();

        await database.SetEventRangeMarkAsync(key, "acme", null, Cancellation);
        (await database.FetchEventRangeMarkAsync(key, "globex", Cancellation)).ShouldBe(20);
    }

    [Fact]
    public async Task a_long_key_round_trips()
    {
        // backfill:{projection}:{name}:cursor with a namespaced projection name
        var database = await DatabaseAsync();
        var key = EventRangeMarks.CursorKey(EventRangeMarks.BackfillKey(
            "Some.Rather.Long.Namespace.OrderSummaryProjection" + Guid.NewGuid().ToString("N"),
            "year-and-line-count"));

        await database.SetEventRangeMarkAsync(key, "a-tenant-with-a-long-identifier", 123456789012, Cancellation);

        (await database.FetchEventRangeMarkAsync(key, "a-tenant-with-a-long-identifier", Cancellation))
            .ShouldBe(123456789012);
    }

    [Fact]
    public async Task a_mark_survives_cleaning_the_event_data()
    {
        // A mark must outlive what a reset of the event data removes, as it outlives rebuild teardown
        var database = await DatabaseAsync();
        var key = AKey();
        await database.TrySetEventRangeMarkAsync(key, null, 500, Cancellation);

        await theFixture.CleanEventDataAsync();

        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldBe(500);
    }
}
