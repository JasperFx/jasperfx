using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Self-aggregating types whose evolvers were emitted by the source generator have to be usable
/// without ever being registered — no <c>Snapshot&lt;T&gt;()</c>, no explicit projection. The store
/// finds them by walking loaded assemblies for <c>[GeneratedEvolver]</c> at construction time.
/// </summary>
/// <remarks>
/// Deliberately configures a store with nothing registered at all, which is what separates this from
/// <see cref="SelfAggregatingEvolveCompliance{TFixture,TOperations,TQuerySession}"/> — there the same
/// aggregates are registered as inline snapshots, so discovery is never exercised.
/// </remarks>
public abstract class AutoDiscoveredAggregateCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_auto_discover";
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public void self_aggregating_types_are_auto_discovered()
    {
        var aggregateTypes = theFixture.AllAggregateTypes().ToArray();

        aggregateTypes.ShouldContain(typeof(MutableIEventEvolveAggregate),
            "MutableIEventEvolveAggregate has a source-generated evolver and was never registered, " +
            "so it can only be here by assembly discovery");
    }

    [Fact]
    public async Task auto_discovered_type_works_for_live_aggregation()
    {
        await using var session = OpenSession();

        var streamId = Guid.NewGuid();
        EventsFor(session).StartStream(streamId, new EvolveAEvent(), new EvolveBEvent(), new EvolveCEvent());
        await SaveChangesAsync(session);

        var aggregate =
            await EventsFor(session).AggregateStreamAsync<MutableIEventEvolveAggregate>(streamId, token: Cancellation);

        aggregate.ShouldNotBeNull();
        aggregate.ACount.ShouldBe(1);
        aggregate.BCount.ShouldBe(1);
        aggregate.CCount.ShouldBe(1);
    }
}
