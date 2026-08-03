namespace JasperFx.Events.TestSupport;

internal abstract class ScenarioStep<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    public string Description { get; set; } = string.Empty;

    public abstract Task Execute(ProjectionScenario<TOperations, TQuerySession> scenario,
        CancellationToken ct = default);
}

internal class ScenarioAction<TOperations, TQuerySession>: ScenarioStep<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Action<IEventOperations> _action;

    public ScenarioAction(Action<IEventOperations> action)
    {
        _action = action;
    }

    public override async Task Execute(ProjectionScenario<TOperations, TQuerySession> scenario,
        CancellationToken ct = default)
    {
        _action(scenario.SessionEvents);

        if (scenario.NextStep is ScenarioAssertion<TOperations, TQuerySession>)
        {
            await scenario.CommitAsync(ct).ConfigureAwait(false);
            await scenario.AwaitNonStaleDataAsync(ct).ConfigureAwait(false);
        }
    }
}

internal class ScenarioAssertion<TOperations, TQuerySession>: ScenarioStep<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Func<TQuerySession, CancellationToken, Task> _check;

    public ScenarioAssertion(Func<TQuerySession, CancellationToken, Task> check)
    {
        _check = check;
    }

    public override Task Execute(ProjectionScenario<TOperations, TQuerySession> scenario,
        CancellationToken ct = default)
    {
        return _check(scenario.QuerySession, ct);
    }
}
