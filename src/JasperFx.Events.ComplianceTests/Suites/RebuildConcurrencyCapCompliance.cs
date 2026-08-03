using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Resolution of the per-database rebuild concurrency cap that stores surface through
/// <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/> — jasperfx#420 / marten#4710. An
/// explicit setting wins; a non-positive setting disables the cap; otherwise the store derives one
/// from its connection pool ceiling, an eighth of the pool with a floor of one.
/// </summary>
/// <remarks>
/// Every test configures its own store, so this suite skips the standard build in
/// <see cref="InitializeAsync"/> rather than paying for a store nothing uses. Pool sizes are
/// expressed store-neutrally through <see cref="ComplianceStoreConfig.MaxPoolSize"/> — the fixture
/// owns the connection string and knows whether it speaks Npgsql or SqlClient.
/// </remarks>
public abstract class RebuildConcurrencyCapCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string Schema = "compliance_rebuild_cap";

    private static readonly Action<ComplianceStoreConfig> _capOfThree = config =>
    {
        config.SchemaName = Schema;
        config.MaxConcurrentRebuildsPerDatabase = 3;
    };

    private static readonly Action<ComplianceStoreConfig> _capOfZero = config =>
    {
        config.SchemaName = Schema;
        config.MaxConcurrentRebuildsPerDatabase = 0;
    };

    private static readonly Action<ComplianceStoreConfig> _capOfSix = config =>
    {
        config.SchemaName = Schema;
        config.MaxConcurrentRebuildsPerDatabase = 6;
    };

    private static readonly Action<ComplianceStoreConfig> _largePool = config =>
    {
        config.SchemaName = Schema;
        config.MaxPoolSize = 64;
    };

    private static readonly Action<ComplianceStoreConfig> _tinyPool = config =>
    {
        config.SchemaName = Schema;
        config.MaxPoolSize = 5;
    };

    protected override Action<ComplianceStoreConfig> Configuration => _capOfThree;

    /// <summary>
    /// Skips the base class's standard build and per-test data cleanup: each test below builds the
    /// store it needs and none of them write events.
    /// </summary>
    public override ValueTask InitializeAsync() => theFixture.InitializeAsync();

    [Fact]
    public async Task configured_value_wins_over_derived_default()
    {
        await theFixture.ConfigureAsync(_capOfThree);

        EventStore.MaxConcurrentRebuildsPerDatabase.ShouldBe(3);
    }

    [Fact]
    public async Task non_positive_configured_value_disables_the_cap()
    {
        await theFixture.ConfigureAsync(_capOfZero);

        EventStore.MaxConcurrentRebuildsPerDatabase.ShouldBeNull();
    }

    [Fact]
    public async Task derived_default_is_pool_size_over_eight()
    {
        await theFixture.ConfigureAsync(_largePool);

        EventStore.MaxConcurrentRebuildsPerDatabase.ShouldBe(8);
    }

    [Fact]
    public async Task derived_default_floors_at_one_for_tiny_pools()
    {
        await theFixture.ConfigureAsync(_tinyPool);

        EventStore.MaxConcurrentRebuildsPerDatabase.ShouldBe(1);
    }

    [Fact]
    public async Task usage_descriptor_carries_the_effective_cap()
    {
        // jasperfx#434: CritterWatch's rebuild dispatcher reads the effective cap off the
        // EventStoreUsage descriptor rather than guessing at it.
        await theFixture.ConfigureAsync(_capOfSix);

        var usage = await EventStore.TryCreateUsage(Cancellation);

        usage.ShouldNotBeNull();
        usage.MaxConcurrentRebuildsPerDatabase.ShouldBe(6);
    }
}
