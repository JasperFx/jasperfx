namespace JasperFx.Events.Projections;

public abstract class EventRangeGroup<TBatch, TDatabase>: IDisposable
{
    private readonly CancellationToken _parent;
    private CancellationTokenSource _cancellationTokenSource;

    protected EventRangeGroup(EventRange range, CancellationToken parent)
    {
        _parent = parent;
        Range = range;
        Agent = range.Agent ?? throw new ArgumentOutOfRangeException(nameof(range), "Agent cannot be null");
    }

    // TODO -- pull this into the constructor later
    public ISubscriptionAgent Agent { get; }

    public EventRange Range { get; }

    public bool WasAborted { get; private set; }

    public CancellationToken Cancellation { get; private set; }
    public int Attempts { get; private set; } = -1;

    public abstract void Dispose();

    /// <summary>
    ///     Teardown any existing state. Used to clean off existing work
    ///     before doing retries
    /// </summary>
    [Obsolete("Get rid of this. Wrong place for this responsibility")]
    public void Reset()
    {
        Attempts++;
        WasAborted = false;
        _cancellationTokenSource = new CancellationTokenSource();

        Cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_parent, _cancellationTokenSource.Token).Token;
        reset();
    }

    protected abstract void reset();

    public abstract Task ConfigureUpdateBatch(TBatch batch);
    public abstract ValueTask SkipEventSequence(long eventSequence, TDatabase database);
}

