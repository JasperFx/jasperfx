using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Grouping;

/// <summary>
/// Structure to hold and help organize events in "slices" by identity to apply
/// to the matching aggregate document TDoc. Note that TDoc might be a marker type.
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class SliceGroup<TDoc, TId> : IEventGrouping<TId> where TId : notnull
{
    public LightweightCache<TId, EventSlice<TDoc, TId>> Slices { get; }
    
    public string TenantId { get; }

    public SliceGroup(string tenantId)
    {
        TenantId = tenantId;
        Slices = new LightweightCache<TId, EventSlice<TDoc, TId>>(id => new EventSlice<TDoc, TId>(id, tenantId));
    }

    /// <summary>
    /// Really only for testing
    /// </summary>
    public SliceGroup() : this(StorageConstants.DefaultTenantId)
    {
    }

    /// <summary>
    ///     Add events to streams where each event of type TEvent applies to only
    ///     one stream
    /// </summary>
    /// <param name="singleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEvents<TEvent>(Func<TEvent, TId> singleIdSource, IEnumerable<IEvent> events) where TEvent : notnull
    {
        if (typeof(TEvent).Closes(typeof(IEvent<>)))
        {
            var matching = events.OfType<TEvent>();
            foreach (var @event in matching)
            {
                var id = singleIdSource(@event);
                AddEvent(id, (IEvent)@event);
            }
        }
        else if (typeof(TEvent) == typeof(IEvent))
        {
            foreach (var @event in events)
            {
                var id = singleIdSource((TEvent)@event);
                AddEvent(id, @event);
            }
        }
        else
        {
            var matching = events.OfType<IEvent<TEvent>>();
            foreach (var @event in matching)
            {
                var id = singleIdSource(@event.Data);
                AddEvent(id, @event);
            }
        }
    }

    /// <summary>
    ///     Apply "fan out" operations to the given TSource type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream just after any instance of the parent
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public void FanOutOnEach<TSource, TChild>(Func<TSource, IEnumerable<TChild>> fanOutFunc) where TSource : notnull where TChild : notnull
    {
        foreach (var slice in Slices)
        {
            slice.FanOut(fanOutFunc);
        }
    }

    /// <summary>
    ///     Add events to multiple slices where each event of type TEvent may be related to many
    ///     different aggregates
    /// </summary>
    /// <param name="multipleIdSource"></param>
    /// <param name="events"></param>
    /// <typeparam name="TEvent"></typeparam>
    public void AddEvents<TEvent>(Func<TEvent, IEnumerable<TId>> multipleIdSource, IEnumerable<IEvent> events) where TEvent : notnull
    {
        if (typeof(TEvent).Closes(typeof(IEvent<>)))
        {
            var matching = events.OfType<TEvent>();
            foreach (var @event in matching)
            {
                foreach (var id in multipleIdSource(@event))
                {
                    AddEvent(id, (IEvent)@event);
                }
            }
        }
        else
        {
            var matching = events.OfType<IEvent<TEvent>>();
            foreach (var @event in matching)
            {
                foreach (var id in multipleIdSource(@event.Data))
                {
                    AddEvent(id, (IEvent)@event);
                }
            }
        }
    }

    /// <summary>
    ///     Add a single event to a single event slice by id
    /// </summary>
    /// <param name="id">The aggregate id</param>
    /// <param name="event"></param>
    public void AddEvent(TId id, IEvent @event)
    {
        if (id != null)
        {
            Slices[id].AddEvent(@event);
        }

    }

    /// <summary>
    ///     Add many events to a single event slice by aggregate id
    /// </summary>
    /// <param name="id">The aggregate id</param>
    /// <param name="events"></param>
    public void AddEvents(TId id, IEnumerable<IEvent> events)
    {
        if (id != null)
        {
            Slices[id].AddEvents(events);
        }
    }

    public void ApplyFanOutRules(List<IFanOutRule> fanoutRules)
    {
        foreach (var slice in Slices)
        {
            slice.ApplyFanOutRules(fanoutRules);
        }
    }

    // Used by composite projections to relay aggregate cache dependencies
    internal List<ISubscriptionExecution> Upstream { get; set; } = [];
    internal IStorageOperations? Operations { get; set; }

    /// <summary>
    /// Apply "enrichment" to the event groups to add contextual inforation with data
    /// lookups from other data in the system or upstream projections
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public EntityStep<TEntity> EnrichWith<TEntity>()
    {
        if (Operations == null)
        {
            throw new InvalidOperationException("This method can only be used within projection execution");
        }
        
        return new EntityStep<TEntity>(this, Operations);
    }

    public class EntityStep<TEntity>(SliceGroup<TDoc, TId> parent, IStorageOperations session)
    {
        /// <summary>
        /// On which event type can you find an identity of type TId for the entity TDoc?
        /// This can be a marker interface. Think of this as equivalent to the LINQ OfType<T>() operator
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <returns></returns>
        public EventStep<TEntity, TEvent> ForEvent<TEvent>() => new(parent, session);
    }

    public class EventStep<TEntity, TEvent>(SliceGroup<TDoc, TId> parent, IStorageOperations session)
    {
        /// <summary>
        /// Specify *how* the enrichment can find the identity TId from the events of type TEvent for entities
        /// of type TEntity
        /// </summary>
        /// <param name="identitySource"></param>
        /// <typeparam name="TEntityId"></typeparam>
        /// <returns></returns>
        public IdentityStep<TEntity, TEvent, TEntityId> ForEntityId<TEntityId>(
            Func<TEvent, TEntityId> identitySource) =>
            new(parent, session, identitySource);
    }

    public class IdentityStep<TEntity, TEvent, TEntityId>(
        SliceGroup<TDoc, TId> parent,
        IStorageOperations session,
        Func<TEvent, TEntityId> identitySource)
    {
        /// <summary>
        /// Apply the enrichment logic
        /// </summary>
        /// <param name="application"></param>
        public async Task EnrichAsync(
            Action<EventSlice<TDoc, TId>, IEvent<TEvent>, TEntity> application)
        {
            var cache = await FetchEntitiesAsync();

            foreach (EventSlice<TDoc, TId> eventSlice in parent.Slices)
            {
                var events = eventSlice.Events().OfType<IEvent<TEvent>>().ToArray();
                foreach (var @event in events)
                {
                    var id = identitySource(@event.Data);
                    if (cache.TryFind(id, out var entity))
                    {
                        application(eventSlice, @event, entity);
                    }
                }
            }
        }

        internal async Task<IAggregateCache<TEntityId, TEntity>> FetchEntitiesAsync()
        {
            var cache = findCache();
            
            var storage = await session.FetchProjectionStorageAsync<TEntity, TEntityId>(parent.TenantId, CancellationToken.None);
            var events = parent.Slices.SelectMany(x => x.Events());
            
            var ids = events.OfType<IEvent<TEvent>>().Select(x => identitySource(x.Data)).ToArray();
            if (!ids.Any())
            {
                return new NulloAggregateCache<TEntityId, TEntity>();
            }
            
            if (cache == null)
            {
                var dict = await storage.LoadManyAsync(ids, CancellationToken.None);
                return new DictionaryAggregateCache<TEntityId, TEntity>(dict);
            }

            var toLoad = ids.Where(id => !cache.Contains(id)).ToArray();
            if (!toLoad.Any()) return cache;
            
            var loaded = await storage.LoadManyAsync(toLoad, CancellationToken.None);

            foreach (var pair in loaded)
            {
                cache.Store(pair.Key, pair.Value);
            }

            return cache;
        }

        private IAggregateCache<TEntityId, TEntity>? findCache()
        {
            foreach (var execution in parent.Upstream)
            {
                if (execution.TryGetAggregateCache<TEntityId, TEntity>(out var cache))
                {
                    return cache.CacheFor(parent.TenantId);
                }
            }

            return null;
        }
    }
}
