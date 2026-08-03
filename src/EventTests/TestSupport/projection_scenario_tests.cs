using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.TestSupport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace EventTests.TestSupport;

public class projection_scenario_tests
{
    private readonly FakeProjectionScenario theScenario = new();

    [Fact]
    public async Task deletes_existing_data_by_default()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        theScenario.Cleaned.ShouldBeTrue();
    }

    [Fact]
    public async Task can_opt_out_of_deleting_existing_data()
    {
        theScenario.DeleteExistingData = false;
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        theScenario.Cleaned.ShouldBeFalse();
    }

    [Fact]
    public async Task actions_are_deferred_until_execution_and_flow_to_the_session_events()
    {
        var streamId = Guid.NewGuid();
        var @event = new AEvent();

        theScenario.Append(streamId, @event);
        theScenario.Events.DidNotReceiveWithAnyArgs().Append(Guid.Empty, Array.Empty<object>());

        await theScenario.ExecuteAsync();

        theScenario.Events.Received().Append(streamId, Arg.Is<object[]>(x => x.Single() == @event));
    }

    [Fact]
    public async Task start_stream_returns_the_generated_stream_id_used_by_the_queued_operation()
    {
        var @event = new AEvent();
        var streamId = theScenario.StartStream<FakeAggregate>(@event);

        streamId.ShouldNotBe(Guid.Empty);

        await theScenario.ExecuteAsync();

        theScenario.Events.Received().StartStream<FakeAggregate>(streamId,
            Arg.Is<object[]>(x => x.Single() == @event));
    }

    [Fact]
    public async Task flushes_before_each_assertion_and_once_at_the_end()
    {
        theScenario.Documents[1] = new FakeDocument();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.Append(Guid.NewGuid(), new AEvent());

        await theScenario.ExecuteAsync();

        // Once before the assertion, once for the trailing append (see marten#5126)
        theScenario.SaveCount.ShouldBe(2);
    }

    [Fact]
    public async Task an_arrange_only_scenario_still_commits()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());

        await theScenario.ExecuteAsync();

        theScenario.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task failed_assertions_accumulate_and_surface_as_typed_assertion_exceptions()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.DocumentShouldExist<FakeDocument>(2);

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(() => theScenario.ExecuteAsync());

        ex.InnerExceptions.Count.ShouldBe(2);
        ex.InnerExceptions.ShouldAllBe(x => x is ProjectionScenarioAssertionException);
        ex.Message.ShouldContain("FAILED");
    }

    [Fact]
    public async Task document_should_not_exist_fails_when_the_document_is_there()
    {
        theScenario.Documents[1] = new FakeDocument();
        theScenario.DocumentShouldNotExist<FakeDocument>(1);

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(() => theScenario.ExecuteAsync());

        ex.InnerExceptions.Single().ShouldBeOfType<ProjectionScenarioAssertionException>();
    }

    [Fact]
    public async Task document_should_exist_runs_the_optional_additional_assertions()
    {
        theScenario.Documents[1] = new FakeDocument { Name = "right" };
        var observed = "";
        theScenario.DocumentShouldExist<FakeDocument>(1, doc => observed = doc.Name);

        await theScenario.ExecuteAsync();

        observed.ShouldBe("right");
    }

    [Fact]
    public async Task a_failed_action_stops_the_scenario_and_skips_the_remaining_steps()
    {
        theScenario.Documents[1] = new FakeDocument();

        theScenario.AppendEvents("blows up", _ => throw new DivideByZeroException("boom"));
        theScenario.DocumentShouldNotExist<FakeDocument>(2);
        theScenario.DocumentShouldExist<FakeDocument>(3);

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(() => theScenario.ExecuteAsync());

        ex.InnerExceptions.Single().ShouldBeOfType<DivideByZeroException>();
        ex.Message.ShouldContain("Skipped the remaining 2 step(s)");

        // No final flush either -- the session may hold a partially built unit of work
        theScenario.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_scenario_cannot_be_executed_twice()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => theScenario.ExecuteAsync());
    }

    [Fact]
    public async Task uses_no_daemon_when_there_are_no_async_projections()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        await theScenario.FakeDaemon.DidNotReceive().StartAllAsync();
    }

    [Fact]
    public async Task starts_waits_on_and_stops_the_daemon_when_async_projections_exist()
    {
        theScenario.HasAsync = true;
        theScenario.Timeout = 5.Seconds();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        await theScenario.FakeDaemon.Received().StartAllAsync();
        await theScenario.FakeDaemon.Received().WaitForNonStaleData(5.Seconds());
        await theScenario.FakeDaemon.Received().StopAllAsync();
    }

    [Fact]
    public async Task tenant_id_flows_to_the_session_and_daemon()
    {
        theScenario.HasAsync = true;
        theScenario.TenantId = "purple";

        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        theScenario.OpenedTenant.ShouldBe("purple");
        theScenario.DaemonTenant.ShouldBe("purple");
    }

    [Fact]
    public async Task the_session_is_disposed_after_execution()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();

        await theScenario.Operations.Received().DisposeAsync();
    }

    public class AEvent;

    public class FakeAggregate;

    public class FakeDocument
    {
        public string Name { get; set; } = "";
    }
}

public interface IFakeQuerySession;

public interface IFakeOperations: IFakeQuerySession, IStorageOperations;

/// <summary>
/// Closes the scenario over throwaway session interfaces so the sequencing, flushing, and
/// failure-handling logic can be exercised without a running store.
/// </summary>
public class FakeProjectionScenario: ProjectionScenario<IFakeOperations, IFakeQuerySession>
{
    public IFakeOperations Operations { get; } = Substitute.For<IFakeOperations>();
    public IEventOperations Events { get; } = Substitute.For<IEventOperations>();
    public IProjectionDaemon FakeDaemon { get; } = Substitute.For<IProjectionDaemon>();

    public Dictionary<object, object> Documents { get; } = new();

    public bool HasAsync { get; set; }
    public bool Cleaned { get; private set; }
    public int SaveCount { get; private set; }
    public string? OpenedTenant { get; private set; }
    public string? DaemonTenant { get; private set; }

    protected override Task DeleteExistingDataAsync(CancellationToken ct)
    {
        Cleaned = true;
        return Task.CompletedTask;
    }

    protected override bool HasAnyAsyncProjections => HasAsync;

    protected override ValueTask<IProjectionDaemon> BuildDaemonAsync(string? tenantId)
    {
        DaemonTenant = tenantId;
        return new ValueTask<IProjectionDaemon>(FakeDaemon);
    }

    protected override IFakeOperations OpenSession(string? tenantId)
    {
        OpenedTenant = tenantId;
        return Operations;
    }

    protected override Task SaveChangesAsync(IFakeOperations session, CancellationToken ct)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    protected override IEventOperations EventsFor(IFakeOperations session) => Events;

    protected override Task<T?> LoadDocumentAsync<T>(IFakeQuerySession session, object id, CancellationToken ct)
        where T : class
    {
        return Task.FromResult(Documents.TryGetValue(id, out var doc) ? doc as T : null);
    }
}
