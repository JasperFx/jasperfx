using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests;

public class AgentDistributionDefaultTests
{
    // A bare IEventStore that overrides none of the agent-distribution members, so the assertions below
    // exercise the default interface implementations. A store that does not opt in must behave exactly as
    // before: one agent per shard×database (DistributesAgentsPerTenant false), even per-agent spreading
    // (GroupAgentAssignmentsByDatabase false), and no fan-out bound (MaxNodesPerDatabaseForAgents 1).
    // See jasperfx/wolverine#3280 and JasperFx/marten#4806. Everything else throws — those members are
    // never invoked here.
    private sealed class BareEventStore : IEventStore
    {
        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private readonly IEventStore theStore = new BareEventStore();

    [Fact]
    public void distributes_agents_per_tenant_defaults_to_false()
    {
        theStore.DistributesAgentsPerTenant.ShouldBeFalse();
    }

    [Fact]
    public void group_agent_assignments_by_database_defaults_to_false()
    {
        theStore.GroupAgentAssignmentsByDatabase.ShouldBeFalse();
    }

    [Fact]
    public void max_nodes_per_database_for_agents_defaults_to_one()
    {
        theStore.MaxNodesPerDatabaseForAgents.ShouldBe(1);
    }
}
