namespace JasperFx.Events.Daemon;

/// <summary>
/// Hook invoked after a daemon subscription batch has been committed to the underlying
/// store. Use for fire-and-forget side effects that need to happen exactly once per
/// successful batch — for example flushing webhooks or emitting telemetry once the
/// projection write is durable.
/// </summary>
/// <remarks>
/// Lifted from Polecat's <c>IChangeListener</c> as the canonical shape. Intentionally
/// narrower than Marten's session-level <c>IDocumentSessionListener</c> / <c>IChangeListener</c>:
/// this is purely a daemon post-batch signal and does not carry a change-set payload.
/// </remarks>
public interface IDaemonChangeListener
{
    /// <summary>
    /// Invoked after the daemon has successfully committed a batch of events.
    /// </summary>
    Task AfterCommitAsync(CancellationToken token);
}

/// <summary>
/// Null-object implementation of <see cref="IDaemonChangeListener"/>. Use as a default
/// when no post-commit work is required.
/// </summary>
public sealed class NullDaemonChangeListener : IDaemonChangeListener
{
    public static readonly NullDaemonChangeListener Instance = new();

    private NullDaemonChangeListener()
    {
    }

    public Task AfterCommitAsync(CancellationToken token) => Task.CompletedTask;
}
