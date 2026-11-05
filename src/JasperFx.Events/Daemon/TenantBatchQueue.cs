using JasperFx.Blocks;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Opt-in with <see cref="DaemonSettings.SerializeTenantProjectionBatches"/>: one single-threaded block per
/// projection per store in this process. Every per-tenant agent of that projection, against every database,
/// builds and commits its batches through it one at a time, so only one built batch of the projection is held
/// in memory on the node no matter how many tenant agents are running.
///
/// <para>
/// Each projection execution still runs its own pages in order through its own block, and only hands the
/// build-and-commit of a page to this queue, awaiting its turn. So an agent has at most one batch queued here
/// at any time, the queue is bounded by the agent count, and its FIFO order rotates through the tenants.
/// </para>
/// </summary>
public sealed class TenantBatchQueue
{
    private readonly Block<QueuedBatch> _block;
    private int _waiting;

    internal TenantBatchQueue(string projectionIdentity)
    {
        ProjectionIdentity = projectionIdentity;

        // Unbounded: each agent posts at most one batch at a time, so a bounded channel would only add a
        // second wait in front of the one this queue already is
        _block = new Block<QueuedBatch>(1, Block<QueuedBatch>.Unbounded, executeAsync);
    }

    /// <summary>
    /// The projection, without any tenant, whose per-tenant batches go through this queue
    /// </summary>
    public string ProjectionIdentity { get; }

    /// <summary>
    /// How many batches are waiting for their turn, not counting the one being built or committed
    /// </summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// Wait for this projection's turn on the node, then run <paramref name="work"/>, which builds, commits and
    /// disposes one batch. Returns false without running it if <paramref name="abandon"/> fires while the work
    /// is still waiting; work that has already started always runs to the end.
    /// </summary>
    public async Task<bool> RunAsync(Func<Task> work, CancellationToken abandon)
    {
        if (abandon.IsCancellationRequested)
        {
            return false;
        }

        var batch = new QueuedBatch(work);

        Interlocked.Increment(ref _waiting);
        await _block.PostAsync(batch).ConfigureAwait(false);

        await using (abandon.Register(static x => ((QueuedBatch)x!).Abandon(), batch))
        {
            return await batch.Completion.Task.ConfigureAwait(false);
        }
    }

    private async Task executeAsync(QueuedBatch batch, CancellationToken _)
    {
        Interlocked.Decrement(ref _waiting);

        if (!batch.TryStart())
        {
            return;
        }

        try
        {
            await batch.Work().ConfigureAwait(false);
            batch.Completion.TrySetResult(true);
        }
        catch (Exception e)
        {
            batch.Completion.TrySetException(e);
        }
    }

    private sealed class QueuedBatch
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Abandoned = 2;

        private int _state = Queued;

        public QueuedBatch(Func<Task> work)
        {
            Work = work;
        }

        public Func<Task> Work { get; }

        // Asynchronous continuations, so the awaiting execution never resumes on this queue's only reader
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryStart() => Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;

        public void Abandon()
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Queued) == Queued)
            {
                Completion.TrySetResult(false);
            }
        }
    }
}
