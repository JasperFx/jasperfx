using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The persisted projection start floor on <see cref="IEventDatabase" /> (jasperfx#809): what
/// <c>AsyncOptions.SubscribeFromFirstRun()</c> pins on a first start, what a rebuild replays from, and what an
/// operator sets or clears.
/// </summary>
/// <remarks>
/// <para>
/// Pins the seam itself, the part each store implements: absent until stored, insert-if-absent with the stored
/// winner returned, an unconditional operator set and clear, one floor per tenant, and agreement under
/// concurrent first starts. How the daemon <em>uses</em> a floor (<c>max(progress, floor)</c> on a start, the
/// floor on a rebuild, a stored floor winning over a configured one) is store-agnostic code and is covered in
/// JasperFx's own tests.
/// </para>
/// <para>
/// Every fact uses its own projection name. Floors outlive data cleaning on purpose, because they have to
/// outlive rebuild teardown, so facts cannot share one.
/// </para>
/// </remarks>
public abstract class ProjectionFloorCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_projection_floors";
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<IEventDatabase> DatabaseAsync()
    {
        Assert.SkipUnless(theFixture.SupportsProjectionFloors,
            "This event store does not implement the projection start-floor seam (jasperfx#809).");
        return (await theFixture.EventStore.AllDatabases()).First();
    }

    private static string AProjection() => "Floor" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task there_is_no_floor_until_one_is_stored()
    {
        var database = await DatabaseAsync();

        (await database.FetchProjectionFloorAsync(AProjection(), null, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task the_first_try_set_stores_the_floor_and_returns_it()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();

        (await database.TrySetProjectionFloorAsync(projection, null, 500, Cancellation)).ShouldBe(500);
        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(500);
    }

    [Fact]
    public async Task a_later_try_set_returns_the_stored_winner_and_changes_nothing()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();
        await database.TrySetProjectionFloorAsync(projection, null, 500, Cancellation);

        (await database.TrySetProjectionFloorAsync(projection, null, 900, Cancellation)).ShouldBe(500);
        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(500);
    }

    [Fact]
    public async Task concurrent_first_starts_agree_on_one_floor()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();

        var winners = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(i => database.TrySetProjectionFloorAsync(projection, null, i * 100, Cancellation)));

        winners.Distinct().Count().ShouldBe(1);
        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(winners[0]);
    }

    [Fact]
    public async Task an_operator_set_overwrites_the_stored_floor()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();
        await database.TrySetProjectionFloorAsync(projection, null, 500, Cancellation);

        await database.SetProjectionFloorAsync(projection, null, 42, Cancellation);

        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(42);
    }

    [Fact]
    public async Task an_operator_set_works_without_a_prior_floor()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();

        await database.SetProjectionFloorAsync(projection, null, 42, Cancellation);

        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(42);
    }

    [Fact]
    public async Task clearing_the_floor_removes_it_so_the_next_first_start_pins_again()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();
        await database.TrySetProjectionFloorAsync(projection, null, 500, Cancellation);

        await database.SetProjectionFloorAsync(projection, null, null, Cancellation);

        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBeNull();
        (await database.TrySetProjectionFloorAsync(projection, null, 700, Cancellation)).ShouldBe(700);
    }

    [Fact]
    public async Task each_tenant_has_its_own_floor()
    {
        var database = await DatabaseAsync();
        var projection = AProjection();

        await database.TrySetProjectionFloorAsync(projection, "acme", 10, Cancellation);
        await database.TrySetProjectionFloorAsync(projection, "globex", 20, Cancellation);

        (await database.FetchProjectionFloorAsync(projection, "acme", Cancellation)).ShouldBe(10);
        (await database.FetchProjectionFloorAsync(projection, "globex", Cancellation)).ShouldBe(20);
        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task the_floor_survives_cleaning_the_event_data()
    {
        // The floor must outlive what a reset of the event data removes, as it outlives rebuild teardown
        var database = await DatabaseAsync();
        var projection = AProjection();
        await database.TrySetProjectionFloorAsync(projection, null, 500, Cancellation);

        await theFixture.CleanEventDataAsync();

        (await database.FetchProjectionFloorAsync(projection, null, Cancellation)).ShouldBe(500);
    }
}
