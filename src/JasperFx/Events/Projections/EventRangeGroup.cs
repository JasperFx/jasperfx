using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

[Obsolete("Going to fold all of this into just EventRange")]
public abstract class EventRangeGroup: IDisposable
{
    protected EventRangeGroup(EventRange range)
    {
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


    [Obsolete("Get rid of this. Wrong place for this responsibility")]
    protected abstract void reset();

    public abstract ValueTask SkipEventSequence(long eventSequence);
}

