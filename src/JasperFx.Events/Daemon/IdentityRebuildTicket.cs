namespace JasperFx.Events.Daemon;

/// <summary>
/// One per-identity rebuild on its way through a shard (jasperfx#1201): posted by the agent, prepared off
/// the loop, bound to a batch, and completed when that batch commits.
/// </summary>
internal sealed class IdentityRebuildTicket
{
    private readonly TaskCompletionSource<IdentityRebuildResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IdentityRebuildTicket(string projectionName, IdentityRebuildRequest request)
    {
        ProjectionName = projectionName;
        Request = request;
    }

    public string ProjectionName { get; }

    public IdentityRebuildRequest Request { get; }

    public Task<IdentityRebuildResult> Task => _completion.Task;

    public bool IsFinished => _completion.Task.IsCompleted;

    // 0 = posted, 1 = handed to the execution, 2 = abandoned before it got there
    private int _state;

    /// <summary>The agent's command loop takes the ticket. False if the shard stopped first.</summary>
    public bool TryMarkSubmitted() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>The shard stopped before its command loop took the ticket. False if it already had.</summary>
    public bool TryMarkAbandoned() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    /// <summary>The ceiling of the batch this ticket is bound to, once it is.</summary>
    public long BoundCeiling { get; set; }

    /// <summary>What the bound batch wrote for the identity.</summary>
    public ActionType Action { get; set; } = ActionType.Nothing;

    public bool TryComplete()
    {
        try
        {
            Request.OnCommitted?.Invoke();
        }
        catch (Exception e)
        {
            // The rebuild committed; the caller's own follow-up failing does not undo it
            return _completion.TrySetException(e);
        }

        return _completion.TrySetResult(new IdentityRebuildResult(BoundCeiling, Action));
    }

    public bool TryFault(Exception e) => _completion.TrySetException(e);

    public IdentityRebuildRefusedException Refusal(IdentityRebuildRefusal reason, string? detail = null)
        => new(ProjectionName, Request.Identity, reason, detail);
}

/// <summary>
/// The execution-side half of a per-identity rebuild (jasperfx#1201). Only a single-stream
/// <see cref="AggregationRunner{TDoc,TId,TOperations,TQuerySession}" /> answers it.
/// </summary>
internal interface IIdentityRebuilder
{
    /// <summary>
    /// Can this runner rebuild an identity? Null when it can, otherwise why not.
    /// </summary>
    IdentityRebuildRefusal? CanRebuildIdentities { get; }

    /// <summary>
    /// Take a request. <paramref name="committedPosition" /> is the shard's committed position at this
    /// moment; a long stream is pre-folded up to it off the loop. <paramref name="whenReady" /> runs once the
    /// request can be bound to a batch.
    /// </summary>
    void Submit(IdentityRebuildTicket ticket, long committedPosition, Func<Task> whenReady);

    /// <summary>Is there a ready request no batch has taken yet?</summary>
    bool HasUnboundRequests { get; }

    /// <summary>Fault every request bound to the batch that just failed.</summary>
    void FaultBound(Exception exception);

    /// <summary>Refuse every request this runner holds, bound or not, because the shard stopped.</summary>
    void AbandonAll();
}
