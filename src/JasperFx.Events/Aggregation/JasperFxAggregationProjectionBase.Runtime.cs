using JasperFx.Core;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
{
    private Func<TDoc?, TId, TQuerySession, IReadOnlyList<IEvent>, CancellationToken, ValueTask<TDoc?>> _evolve;
    private Func<TQuerySession, TDoc?, TId, IIdentitySetter<TDoc, TId>, IReadOnlyList<IEvent>, CancellationToken,
        ValueTask<SnapshotAction<TDoc>>> _buildAction;
    
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

    /// <summary>
    /// Override this method to apply workflow mechanics to your aggregate with
    /// a purely synchronous method that does not require any additional data
    /// lookup.
    ///
    /// It is valid to override this method or ApplyAsync, but not both!
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="identity"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    [JasperFxIgnore]
    // TODO -- reconsider this method name. 
    public virtual SnapshotAction<TDoc> DetermineAction(TDoc? snapshot, TId identity, IReadOnlyList<IEvent> events)
    {
        throw new NotImplementedException("Did you forget to implement this?");
    }
    
    // TODO -- inline this and just use _buildAction?
    public virtual ValueTask<SnapshotAction<TDoc>> DetermineActionAsync(TQuerySession session,
        TDoc? snapshot,
        TId identity,
        IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        return _buildAction(session, snapshot, identity, identitySetter, events, cancellation);
    }

    private async ValueTask<SnapshotAction<TDoc>> buildActionAsync(TQuerySession session, TDoc? snapshot, TId identity, IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        // Does the aggregate already exist before the events are applied?
        var exists = snapshot != null;

        if (MatchesAnyDeleteType(events))
        {
            if (!exists) return new Nothing<TDoc>(snapshot);

            return new Delete<TDoc, TId>(snapshot, identity);
        }
        
        snapshot = await _evolve(snapshot, identity, session, events, cancellation);
        (_, snapshot) = tryApplyMetadata(events, snapshot, identity, identitySetter);

        if (snapshot == null)
        {
            return exists ? new Delete<TDoc, TId>(snapshot, identity) : new Nothing<TDoc>(snapshot);
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