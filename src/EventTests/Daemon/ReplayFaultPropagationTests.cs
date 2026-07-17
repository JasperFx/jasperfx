using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Daemon;

// A critical failure during a rebuild faults the agent's rebuild TaskCompletionSource
// (ReportCriticalFailureAsync -> _rebuild.SetException) with the clear intent of failing the
// ReplayAsync caller — and through it RebuildProjectionAsync and the CLI `projections rebuild`
// command, whose error handling only fires on a thrown exception. TimeoutAfterAsync used to swallow
// that fault, so a failed rebuild returned as if it had succeeded.
public class ReplayFaultPropagationTests
{
    [Fact]
    public async Task a_critical_failure_during_a_rebuild_faults_ReplayAsync()
    {
        var execution = Substitute.For<ISubscriptionExecution>();
        var loader = Substitute.For<IEventLoader>();
        loader.LoadAsync(Arg.Any<EventRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DivideByZeroException("Configured loader failure"));

        var agent = new SubscriptionAgent(new ShardName("Projection1"), new AsyncOptions(),
            TimeProvider.System, loader, execution, new ShardStateTracker(NullLogger.Instance),
            Substitute.For<ISubscriptionMetrics>(), NullLogger.Instance);

        var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild,
            new ErrorHandlingOptions(), new NulloDaemonRuntime());

        var thrown = await Should.ThrowAsync<DivideByZeroException>(
            agent.ReplayAsync(request, 1000, TimeSpan.FromSeconds(30)));
        thrown.Message.ShouldBe("Configured loader failure");

        agent.Status.ShouldBe(AgentStatus.Paused);
    }
}
