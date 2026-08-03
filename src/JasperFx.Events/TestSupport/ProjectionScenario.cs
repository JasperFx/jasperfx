using JasperFx.Core;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.TestSupport;

/// <summary>
///     Store-agnostic test harness that scripts a sequence of event appends and document
///     assertions against a running event store, then executes the whole sequence --
///     including standing up a projection daemon when any asynchronous projections are
///     registered. Concrete event stores (Marten, Polecat) subclass this to close the
///     generic session pair and implement the small seam of store-specific operations.
/// </summary>
/// <typeparam name="TOperations">
///     The store's writable session type -- Marten <c>IDocumentOperations</c>, Polecat
///     <c>IDocumentSession</c>.
/// </typeparam>
/// <typeparam name="TQuerySession">The store's read-only session type.</typeparam>
/// <remarks>
///     The generic closure mirrors <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> and
///     <c>EventStoreComplianceFixture&lt;TOperations, TQuerySession&gt;</c>: the products
///     deliberately close the pair differently and convergence is a non-goal.
/// </remarks>
public abstract partial class ProjectionScenario<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Queue<ScenarioStep<TOperations, TQuerySession>> _steps = new();
    private TOperations? _session;
    private bool _hasExecuted;

    private IProjectionDaemon? Daemon { get; set; }

    internal ScenarioStep<TOperations, TQuerySession>? NextStep => _steps.Count != 0 ? _steps.Peek() : null;

    internal IEventOperations SessionEvents => EventsFor(_session!);

    internal TQuerySession QuerySession => _session!;

    internal Task CommitAsync(CancellationToken ct)
    {
        return SaveChangesAsync(_session!, ct);
    }

    internal Task AwaitNonStaleDataAsync(CancellationToken ct)
    {
        if (Daemon == null)
        {
            return Task.CompletedTask;
        }

        return Daemon.WaitForNonStaleData(Timeout).WaitAsync(ct);
    }

    /// <summary>
    ///     The scenario deletes all existing event data plus the storage for every
    ///     registered projection before running. Set this to false to run the
    ///     scenario on top of whatever data already exists
    /// </summary>
    public bool DeleteExistingData { get; set; } = true;

    /// <summary>
    ///     Opt into applying this scenario to a specific tenant id in the
    ///     case of using multi-tenancy of any kind
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     Maximum time the scenario waits for any asynchronous projections to
    ///     catch up after each batch of appended events. Default is 30 seconds
    /// </summary>
    public TimeSpan Timeout { get; set; } = 30.Seconds();

    /// <summary>
    ///     Remove all event data plus the storage for every registered projection so the
    ///     scenario starts from a clean slate. Only called when <see cref="DeleteExistingData" />
    ///     is true (the default)
    /// </summary>
    protected abstract Task DeleteExistingDataAsync(CancellationToken ct);

    /// <summary>
    ///     Whether the store has any projections registered with an asynchronous lifecycle.
    ///     When true, the scenario stands up a projection daemon for the duration of the run
    /// </summary>
    protected abstract bool HasAnyAsyncProjections { get; }

    /// <summary>
    ///     Build the store's projection daemon, optionally scoped to a tenant database
    /// </summary>
    protected abstract ValueTask<IProjectionDaemon> BuildDaemonAsync(string? tenantId);

    /// <summary>
    ///     Open a writable session, optionally scoped to a tenant. The scenario disposes it
    /// </summary>
    protected abstract TOperations OpenSession(string? tenantId);

    /// <summary>
    ///     Commit the session's pending work. No shared JasperFx interface declares
    ///     SaveChanges, so each store supplies its own one-liner
    /// </summary>
    protected abstract Task SaveChangesAsync(TOperations session, CancellationToken ct);

    /// <summary>
    ///     The session's event operations, through the shared <see cref="IEventOperations" /> surface
    /// </summary>
    protected abstract IEventOperations EventsFor(TOperations session);

    /// <summary>
    ///     Load a persisted document by id, dispatching on the id's runtime type. This is what
    ///     proves a projection actually wrote something
    /// </summary>
    protected abstract Task<T?> LoadDocumentAsync<T>(TQuerySession session, object id, CancellationToken ct)
        where T : class;

    private ScenarioStep<TOperations, TQuerySession> action(Action<IEventOperations> action)
    {
        var step = new ScenarioAction<TOperations, TQuerySession>(action);
        _steps.Enqueue(step);

        return step;
    }

    private ScenarioStep<TOperations, TQuerySession> assertion(Func<TQuerySession, CancellationToken, Task> check)
    {
        var step = new ScenarioAssertion<TOperations, TQuerySession>(check);
        _steps.Enqueue(step);

        return step;
    }

    /// <summary>
    ///     Execute every queued step in order. Consecutive appends are batched into a single
    ///     commit; the pending work is saved whenever the next step is an assertion, and once
    ///     more at the end of the scenario. A scenario can only be executed once
    /// </summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (_hasExecuted)
        {
            throw new InvalidOperationException(
                "This ProjectionScenario has already been executed and its steps have been consumed. Build and run a new scenario instead");
        }

        _hasExecuted = true;

        if (DeleteExistingData)
        {
            await DeleteExistingDataAsync(ct).ConfigureAwait(false);
        }

        if (HasAnyAsyncProjections)
        {
            Daemon = await BuildDaemonAsync(TenantId).ConfigureAwait(false);
            await Daemon.StartAllAsync().ConfigureAwait(false);
        }

        _session = OpenSession(TenantId);

        try
        {
            var exceptions = new List<Exception>();
            var number = 0;
            var descriptions = new List<string>();
            var actionFailed = false;

            while (_steps.Count != 0)
            {
                number++;
                var step = _steps.Dequeue();

                try
                {
                    await step.Execute(this, ct).ConfigureAwait(false);
                    descriptions.Add($"{number.ToString().PadLeft(3)}. {step.Description}");
                }
                catch (Exception e)
                {
                    descriptions.Add($"FAILED: {number.ToString().PadLeft(3)}. {step.Description}");
                    descriptions.Add(e.ToString());
                    exceptions.Add(e);

                    // A failed action means every later step would run against a state nobody
                    // intended, so stop right here instead of piling up cascading noise. Failed
                    // assertions keep accumulating -- the state is still the intended one.
                    if (step is ScenarioAction<TOperations, TQuerySession>)
                    {
                        actionFailed = true;
                        if (_steps.Count != 0)
                        {
                            descriptions.Add(
                                $"Skipped the remaining {_steps.Count} step(s) after the failed action");
                            _steps.Clear();
                        }

                        break;
                    }
                }
            }

            // An action only flushes when the step AFTER it is an assertion, so whatever a
            // trailing action queued is still sitting in the session -- and the finally below
            // disposes that session without committing. An append with no assertion after it is
            // still an append, and an arrange-only scenario should not be a silent no-op that
            // passes. See marten#5126.
            //
            // Unconditional on purpose: SaveChanges is expected to return immediately when the
            // unit of work is empty, and the non-stale wait is already a no-op when no daemon is
            // running. Skipped when an action failed -- the session may hold a partially built
            // unit of work at that point.
            if (!actionFailed)
            {
                try
                {
                    await CommitAsync(ct).ConfigureAwait(false);
                    await AwaitNonStaleDataAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    descriptions.Add("FAILED: committing the events queued by the final step");
                    descriptions.Add(e.ToString());
                    exceptions.Add(e);
                }
            }

            if (exceptions.Count != 0)
            {
                throw new ProjectionScenarioException(descriptions, exceptions);
            }
        }
        finally
        {
            if (Daemon != null)
            {
                await Daemon.StopAllAsync().ConfigureAwait(false);
                Daemon.SafeDispose();
            }

            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
