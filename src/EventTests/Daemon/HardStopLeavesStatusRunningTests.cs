using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#716: SubscriptionAgent has three stop paths, and only two of them set the agent's own
// Status. StopAndDrainAsync sets Stopped in its finally; ReportCriticalFailureAsync sets Stopped or
// Paused. HardStopAsync publishes AgentStatus="Stopped" to the tracker -- so the persisted extended
// progression row reads Stopped -- and then leaves the object's Status at its initialized Running.
//
// That asymmetry is invisible until something POLLS the agent object rather than the tracker, which is
// exactly what Wolverine's EventSubscriptionAgent does: wolverine GH-3519 made the wrapper's Status
// delegate to the inner agent precisely so NodeAgentController could see a dead shard and restart it.
// A hard-stopped agent reports Running forever, so the wrapper reports Running, AllRunningAgentUris()
// lists it, CheckHealthAsync passes, and nothing ever recovers the shard.
//
// HardStopAsync is what stopRunningAgents / stopRunningAgentsForTenant call at the top of EVERY rebuild,
// and what tryStartAgentAsync calls to tear down a faulted start.
//
// The rebuild path masks it -- rebuildAgent's stopIfRunningAsync reaches the same registered object
// moments later and StopAndDrains it, which does set Stopped -- so the exposure is a caller with
// nothing behind it, the faulted-start teardown being the clearest. That masking is also why this is a
// latent defect rather than the cause of any reported symptom; do not attach one to it without
// building the repro.
public class HardStopLeavesStatusRunningTests
{
    [Fact]
    public async Task hard_stop_sets_the_agent_status_to_stopped()
    {
        await using var harness = new Harness();

        harness.Agent.Status.ShouldBe(AgentStatus.Running);

        await harness.Agent.HardStopAsync();

        harness.Agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task stop_and_drain_sets_the_agent_status_to_stopped()
    {
        // The control: the graceful path already does this, and is why the gap above reads as an
        // oversight rather than a deliberate difference.
        await using var harness = new Harness();

        await harness.Agent.StopAndDrainAsync(CancellationToken.None);

        harness.Agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Tracker = new ShardStateTracker(new NulloLogger());
            Agent = new SubscriptionAgent(new ShardName("Trip"), new AsyncOptions(), TimeProvider.System,
                Substitute.For<IEventLoader>(), Substitute.For<ISubscriptionExecution>(), Tracker,
                Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);
        }

        public ShardStateTracker Tracker { get; }
        public SubscriptionAgent Agent { get; }

        public ValueTask DisposeAsync()
        {
            Tracker.As<IDisposable>().Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
