using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
{
    private Func<TDoc?, TId, TQuerySession, IReadOnlyList<IEvent>, CancellationToken, ValueTask<TDoc?>> _evolve;
    
    
    private async ValueTask<TDoc?> evolveDefaultAsync(TDoc? snapshot, TId id, TQuerySession session,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var @event in events)
        {
            if (@event is IEvent<Archived>)
            {
                return snapshot;
            }

            try
            {
                snapshot = await EvolveAsync(snapshot, id, session, @event, cancellation);
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (IsExceptionTransient(e))
                {
                    throw;
                }

                throw new ApplyEventException(@event, e);
            }
        }

        return snapshot;
    }
    
    private ValueTask<TDoc?> evolveDefault(TDoc? snapshot, TId id, TQuerySession session,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var @event in events)
        {
            if (@event is IEvent<Archived>)
            {
                return new ValueTask<TDoc?>(snapshot);
            }

            try
            {
                snapshot = Evolve(snapshot, id, @event);
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (IsExceptionTransient(e))
                {
                    throw;
                }

                throw new ApplyEventException(@event, e);
            }
        }
        
        return new ValueTask<TDoc?>(snapshot);
    }
    
    public virtual async ValueTask<SnapshotAction<TDoc>> ApplyAsync(TQuerySession session,
        TDoc? snapshot,
        TId identity,
        IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        // Does the aggregate already exist before the events are applied?
        var exists = snapshot != null;

        // TODO -- pass through the identity too
        await _evolve(snapshot, identity, session, events, cancellation);

        if (snapshot == null)
        {
            return exists ? new Delete<TDoc>(snapshot) : new Nothing<TDoc>(snapshot);
        }

        return new Store<TDoc>(snapshot);
    }

    /// <summary>
    ///     Override this method to write explicit logic for this aggregation to evolve or create a snapshot
    ///     based on a single event at a time
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="id"></param>
    /// <param name="session"></param>
    /// <param name="e"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public virtual ValueTask<TDoc?> EvolveAsync(TDoc? snapshot, TId id, TQuerySession session, IEvent e,
        CancellationToken cancellation)
    {
        return snapshot == null
            ? _application.Create(e, session, cancellation)
            : _application.ApplyAsync(snapshot, e, session, cancellation);
    }

    /// <summary>
    /// Override this method to write explicit logic for this aggregation to evolve or create a snapshot
    /// based on a single event at a time using only synchronous code
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="id"></param>
    /// <param name="e"></param>
    /// <returns></returns>
    public virtual TDoc? Evolve(TDoc? snapshot, TId id, IEvent e)
    {
        throw new NotImplementedException();
    }
}