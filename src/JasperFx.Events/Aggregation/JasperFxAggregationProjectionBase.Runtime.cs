using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private Func<TDoc?, TId, TQuerySession, IReadOnlyList<IEvent>, CancellationToken, ValueTask<TDoc?>> _evolve;
    private Func<TQuerySession, TDoc?, TId, IIdentitySetter<TDoc, TId>, IReadOnlyList<IEvent>, CancellationToken,
        ValueTask<(TDoc?, ActionType)>> _buildAction;
    
    private async ValueTask<TDoc?> evolveDefaultAsync(TDoc? snapshot, TId id, TQuerySession session,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var @event in events)
        {
            try
            {
                snapshot = await EvolveAsync(snapshot, id, session, @event, cancellation);
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (ProjectionExceptions.IsExceptionTransient(e))
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
            try
            {
                snapshot = Evolve(snapshot, id, @event);
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (ProjectionExceptions.IsExceptionTransient(e))
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
    public virtual (TDoc?, ActionType) DetermineAction(TDoc? snapshot, TId identity, IReadOnlyList<IEvent> events)
    {
        throw new NotImplementedException("Did you forget to implement this?");
    }
    
    /// <summary>
    /// Override this method if your projection requires complex workflow like deleting or "un-deleting" projected documents
    /// *and* you require asynchronous data access code as part of the workflow. 
    /// </summary>
    /// <param name="session"></param>
    /// <param name="snapshot"></param>
    /// <param name="identity"></param>
    /// <param name="identitySetter"></param>
    /// <param name="events"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [JasperFxIgnore]
    public virtual ValueTask<(TDoc?, ActionType)> DetermineActionAsync(TQuerySession session,
        TDoc? snapshot,
        TId identity,
        IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        return _buildAction(session, snapshot, identity, identitySetter, events, cancellation);
    }

    private async ValueTask<(TDoc?, ActionType)> buildActionAsync(TQuerySession session, TDoc? snapshot, TId identity, IIdentitySetter<TDoc, TId> identitySetter,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        // Does the aggregate already exist before the events are applied?
        var exists = snapshot != null;

        if (MatchesAnyDeleteType(events))
        {
            if (!exists) return (snapshot, ActionType.Nothing);

            return new(snapshot, ActionType.Delete);
        }
        
        snapshot = await _evolve(snapshot, identity, session, events, cancellation);

        if (snapshot == null)
        {
            return exists ? (snapshot, ActionType.Delete) : (snapshot, ActionType.Nothing);
        }

        return (snapshot, ActionType.Store);
    }

    /// <summary>
    /// Override this method to write explicit logic for this aggregation to evolve or create a snapshot
    /// based on a single event at a time.  
    /// Returning <c>null</c> when the document previously existed will cause the document to be deleted.
    /// </summary>
    /// <param name="snapshot">The current snapshot, if it exists</param>
    /// <param name="id">The aggregate identifier</param>
    /// <param name="session">The current query session</param>
    /// <param name="e">The event to apply</param>
    /// <param name="cancellation">Cancellation token</param>
    /// <returns>
    ///     The evolved snapshot, a new snapshot, or <c>null</c>.  
    ///     Returning <c>null</c> when the document previously existed deletes the document,  
    ///     returning <c>null</c> when no document existed does nothing.
    /// </returns>
    [JasperFxIgnore]
    public virtual ValueTask<TDoc?> EvolveAsync(TDoc? snapshot, TId id, TQuerySession session, IEvent e,
        CancellationToken cancellation)
    {
        return (snapshot == null
            ? _application.Create(e, session, cancellation)
            : _application.ApplyAsync(snapshot, e, session, cancellation)!)!;
    }

    /// <summary>
    ///     Override this method to write explicit logic for this aggregation to evolve or create a snapshot
    ///     based on a single event at a time using only synchronous code.  
    ///     Returning <c>null</c> when the document previously existed will cause the document to be deleted.
    /// </summary>
    /// <param name="snapshot">The current snapshot, if it exists</param>
    /// <param name="id">The aggregate identifier</param>
    /// <param name="e">The event to apply</param>
    /// <returns>
    ///     The evolved snapshot, a new snapshot, or <c>null</c>.  
    ///     Returning <c>null</c> when the document previously existed deletes the document,  
    ///     returning <c>null</c> when no document existed does nothing.
    /// </returns>
    [JasperFxIgnore]
    public virtual TDoc? Evolve(TDoc? snapshot, TId id, IEvent e)
    {
        throw new NotImplementedException("Did you forget to implement this?");
    }
}
