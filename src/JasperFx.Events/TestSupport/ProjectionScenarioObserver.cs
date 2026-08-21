namespace JasperFx.Events.TestSupport;

/// <summary>
///     Whether a scripted <see cref="ProjectionScenario{TOperations,TQuerySession}" /> step is an
///     action (an append / start-stream that mutates the store) or an assertion (a check against
///     the projected data). A failed action stops the run; failed assertions accumulate.
/// </summary>
public enum ProjectionScenarioStepKind
{
    /// <summary>An event append or stream start. Consecutive actions batch into one commit.</summary>
    Action,

    /// <summary>A check against the projected data. Runs after the preceding actions are committed and the daemon has caught up.</summary>
    Assertion,
}

/// <summary>
///     The observable face of one scripted step (jasperfx#688). <see cref="Number" /> is the
///     1-based position the step holds in the scenario — the same number the
///     <see cref="ProjectionScenarioException" /> report prints — and <see cref="Description" /> is
///     the same renderable text.
/// </summary>
/// <param name="Number">1-based position of the step in the scenario.</param>
/// <param name="Kind">Action or assertion.</param>
/// <param name="Description">The step's renderable description.</param>
public sealed record ProjectionScenarioStepDescription(
    int Number,
    ProjectionScenarioStepKind Kind,
    string Description);

/// <summary>
///     Observes a <see cref="ProjectionScenario{TOperations,TQuerySession}" /> run step by step
///     (jasperfx#688), so a spec runner can render outcomes live instead of parsing the exception
///     report. Every executed step raises exactly one <see cref="StepStarted" /> followed by exactly
///     one of <see cref="StepSucceeded" /> / <see cref="StepFailed" />; steps the scenario never
///     reaches because an earlier action failed raise <see cref="StepSkipped" /> instead.
/// </summary>
public interface IProjectionScenarioObserver
{
    /// <summary>The step is about to execute.</summary>
    void StepStarted(ProjectionScenarioStepDescription step);

    /// <summary>The step executed without throwing.</summary>
    void StepSucceeded(ProjectionScenarioStepDescription step);

    /// <summary>The step threw. For an action this also ends the run.</summary>
    void StepFailed(ProjectionScenarioStepDescription step, Exception exception);

    /// <summary>The step was never executed because an earlier action failed.</summary>
    void StepSkipped(ProjectionScenarioStepDescription step);
}
