using JasperFx.Events.TestSupport;
using Shouldly;
using Xunit;
using static EventTests.TestSupport.projection_scenario_tests;

namespace EventTests.TestSupport;

// jasperfx#688: the scripted step model is observable — the plan before execution, and one
// StepStarted / StepSucceeded|StepFailed pair per step as it runs, numbered the way the
// exception report numbers them.
public class projection_scenario_observation_tests
{
    private readonly FakeProjectionScenario theScenario = new();
    private readonly RecordingObserver theObserver = new();

    public projection_scenario_observation_tests()
    {
        theScenario.Observer = theObserver;
    }

    [Fact]
    public void planned_steps_are_readable_before_execution_with_kind_number_and_description()
    {
        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.AssertAgainstProjectedData("custom check", (_, _) => Task.CompletedTask);

        var plan = theScenario.PlannedSteps;

        plan.Count.ShouldBe(3);
        plan.Select(x => x.Number).ShouldBe(new[] { 1, 2, 3 });
        plan.Select(x => x.Kind).ShouldBe(new[]
        {
            ProjectionScenarioStepKind.Action,
            ProjectionScenarioStepKind.Assertion,
            ProjectionScenarioStepKind.Assertion,
        });
        plan[0].Description.ShouldStartWith("Append(");
        plan[1].Description.ShouldContain("FakeDocument");
        plan[1].Description.ShouldContain("should exist");
        plan[2].Description.ShouldBe("custom check");

        theObserver.Events.ShouldBeEmpty(); // nothing has run
    }

    [Fact]
    public async Task planned_steps_is_empty_for_an_empty_scenario_and_survives_execution()
    {
        theScenario.PlannedSteps.ShouldBeEmpty();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        var before = theScenario.PlannedSteps;

        await theScenario.ExecuteAsync();

        theScenario.PlannedSteps.ShouldBe(before);
    }

    [Fact]
    public async Task each_step_raises_started_then_succeeded_in_plan_order_with_plan_numbering()
    {
        theScenario.Documents[1] = new FakeDocument();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.Append(Guid.NewGuid(), new AEvent());

        var plan = theScenario.PlannedSteps;

        await theScenario.ExecuteAsync();

        theObserver.Events.ShouldBe(new[]
        {
            ("started", plan[0]),
            ("succeeded", plan[0]),
            ("started", plan[1]),
            ("succeeded", plan[1]),
            ("started", plan[2]),
            ("succeeded", plan[2]),
        });
    }

    [Fact]
    public async Task a_failed_assertion_is_reported_and_the_run_continues()
    {
        // Documents[1] is missing, so step 2 fails; Documents[2] exists, so step 3 passes
        theScenario.Documents[2] = new FakeDocument();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.DocumentShouldExist<FakeDocument>(2);

        var plan = theScenario.PlannedSteps;

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(() => theScenario.ExecuteAsync());

        theObserver.Events.ShouldBe(new[]
        {
            ("started", plan[0]),
            ("succeeded", plan[0]),
            ("started", plan[1]),
            ("failed", plan[1]),
            ("started", plan[2]),
            ("succeeded", plan[2]),
        });
        theObserver.Failures.Single().step.Number.ShouldBe(2);
        theObserver.Failures.Single().exception.ShouldBeOfType<ProjectionScenarioAssertionException>();

        // the same number the exception report prints
        ex.Message.ShouldContain($"FAILED:   2. {plan[1].Description}");
    }

    [Fact]
    public async Task a_failed_action_is_reported_and_every_later_step_is_skipped_with_its_plan_number()
    {
        theScenario.Documents[1] = new FakeDocument();

        theScenario.Append(Guid.NewGuid(), new AEvent());
        theScenario.AppendEvents("boom", _ => throw new InvalidOperationException("boom"));
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.Append(Guid.NewGuid(), new AEvent());

        var plan = theScenario.PlannedSteps;

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(() => theScenario.ExecuteAsync());

        theObserver.Events.ShouldBe(new[]
        {
            ("started", plan[0]),
            ("succeeded", plan[0]),
            ("started", plan[1]),
            ("failed", plan[1]),
            ("skipped", plan[2]),
            ("skipped", plan[3]),
        });
        theObserver.Failures.Single().exception.ShouldBeOfType<InvalidOperationException>();
        ex.Message.ShouldContain("FAILED:   2. boom");
        ex.Message.ShouldContain("Skipped the remaining 2 step(s)");
    }

    [Fact]
    public async Task no_observer_is_fine()
    {
        theScenario.Observer = null;
        theScenario.Append(Guid.NewGuid(), new AEvent());
        await theScenario.ExecuteAsync();
    }

    [Fact]
    public async Task no_observer_and_a_failed_action_still_drains_the_skipped_steps()
    {
        // Regression: the skip loop must dequeue outside the null-conditional observer call,
        // or with no observer the queue never drains and ExecuteAsync never returns.
        theScenario.Observer = null;
        theScenario.AppendEvents("boom", _ => throw new InvalidOperationException("boom"));
        theScenario.DocumentShouldExist<FakeDocument>(1);
        theScenario.DocumentShouldExist<FakeDocument>(2);

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(
            () => theScenario.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        ex.Message.ShouldContain("Skipped the remaining 2 step(s)");
    }

    private sealed class RecordingObserver : IProjectionScenarioObserver
    {
        public List<(string, ProjectionScenarioStepDescription)> Events { get; } = new();
        public List<(ProjectionScenarioStepDescription step, Exception exception)> Failures { get; } = new();

        public void StepStarted(ProjectionScenarioStepDescription step) => Events.Add(("started", step));
        public void StepSucceeded(ProjectionScenarioStepDescription step) => Events.Add(("succeeded", step));

        public void StepFailed(ProjectionScenarioStepDescription step, Exception exception)
        {
            Events.Add(("failed", step));
            Failures.Add((step, exception));
        }

        public void StepSkipped(ProjectionScenarioStepDescription step) => Events.Add(("skipped", step));
    }
}
