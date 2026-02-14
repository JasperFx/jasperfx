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
        if (id != null && !id.Equals(default(TId)))
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
        if (id != null && !id.Equals(default(TId)))
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
    
    private IAggregateCache<TEntityId, TEntity>? findCache<TEntityId, TEntity>()
    {
        foreach (var execution in Upstream)
        {
            if (execution.TryGetAggregateCache<TEntityId, TEntity>(out var cache))
            {
                return cache.CacheFor(TenantId);
            }
        }

        return null;
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

    public class EventStep<TEntity, TEvent>(SliceGroup<TDoc, TId> parent, IStorageOperations session) where TEvent : notnull
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
            new(parent, session, e => identitySource(e.Data));

        /// <summary>
        /// Specify *how* the enrichment can find the identity TId from the events of type TEvent for entities
        /// of type TEntity
        /// </summary>
        /// <param name="identitySource"></param>
        /// <typeparam name="TEntityId"></typeparam>
        /// <returns></returns>
        public IdentityStep<TEntity, TEvent, TEntityId> ForEntityIdFromEvent<TEntityId>(
            Func<IEvent<TEvent>, TEntityId> identitySource) =>
            new(parent, session, identitySource);
        
        /// <summary>
        /// Configure a custom entity loading step that allows full control over how
        /// related entities are fetched from Marten using LINQ or any other query logic.
        /// This is intended for more complex loading scenarios than simple identity based
        /// lookups, for example filtering on additional fields, loading only active
        /// entities, or applying joins and includes.
        /// </summary>
        /// <typeparam name="TEntityId">
        /// The identifier type used to correlate events to loaded entities.
        /// </typeparam>
        /// <param name="loader">
        /// A function that receives the current <see cref="IStorageOperations"/> instance,
        /// the complete set of events of type <typeparamref name="TEvent"/> that occur
        /// in the active slices, and a cancellation token.
        /// The function is responsible for loading the relevant <typeparamref name="TEntity"/>
        /// instances and returning them as a dictionary keyed by <typeparamref name="TEntityId"/>.
        /// </param>
        /// <returns>
        /// A <see cref="QueryStep{TEntity, TEvent, TEntityId}"/> that can be used to apply
        /// enrichment logic based on the loaded entities.
        /// </returns>
        public QueryStep<TEntity, TEvent, TEntityId> UsingEntityQuery<TEntityId>(
            Func<
                IStorageOperations,
                IReadOnlyList<IEvent<TEvent>>,
                CancellationToken,
                Task<IReadOnlyDictionary<TEntityId, TEntity>>> loader)
            where TEntityId : notnull =>
            new(parent, session, loader);
    }
    
    public class QueryStep<TEntity, TEvent, TEntityId>(
        SliceGroup<TDoc, TId> parent,
        IStorageOperations session,
        Func<IStorageOperations, IReadOnlyList<IEvent<TEvent>>, CancellationToken, Task<IReadOnlyDictionary<TEntityId, TEntity>>> loader)
        where TEntityId : notnull where TEvent : notnull
    {
        public async Task EnrichAsync(
            Func<IEvent<TEvent>, TEntityId> eventToEntityId,
            Action<EventSlice<TDoc, TId>, IEvent<TEvent>, TEntity> application,
            CancellationToken ct = default)
        {
            var allEvents = parent.Slices
                .SelectMany(x => x.Events())
                .OfType<IEvent<TEvent>>()
                .ToArray();

            if (allEvents.Length == 0) return;

            var cache = parent.findCache<TEntityId, TEntity>();
            var dict = await loader(session, allEvents, ct);

            foreach (var slice in parent.Slices)
            {
                var events = slice.Events().OfType<IEvent<TEvent>>().ToArray();
                foreach (var e in events)
                {
                    var id = eventToEntityId(e);

                    if (cache != null && cache.TryFind(id, out var cachedEntity))
                    {
                        application(slice, e, cachedEntity);
                        continue;
                    }

                    if (dict.TryGetValue(id, out var entity))
                    {
                        cache?.Store(id, entity);
                        application(slice, e, entity);
                    }
                }
            }
        }
    }

    public class IdentityStep<TEntity, TEvent, TEntityId>(
        SliceGroup<TDoc, TId> parent,
        IStorageOperations session,
        Func<IEvent<TEvent>, TEntityId> identitySource) where TEvent : notnull
    {
        public async Task EnrichAsync(
            Action<EventSlice<TDoc, TId>, IEvent<TEvent>, TEntity> application)
        {
            var cache = await FetchEntitiesAsync();

            foreach (EventSlice<TDoc, TId> eventSlice in parent.Slices)
            {
                var events = eventSlice.Events().OfType<IEvent<TEvent>>().ToArray();
                foreach (var @event in events)
                {
                    var id = identitySource(@event);
                    if (cache.TryFind(id, out var entity))
                    {
                        application(eventSlice, @event, entity);
                    }
                }
            }
        }

        public Task AddReferences()
        {
            return EnrichAsync((slice, _, entity) => slice.Reference(entity));
        }

        internal async Task<IAggregateCache<TEntityId, TEntity>> FetchEntitiesAsync()
        {
            var cache = parent.findCache<TEntityId, TEntity>();
            
            var storage = await session.FetchProjectionStorageAsync<TEntity, TEntityId>(parent.TenantId, CancellationToken.None);
            var events = parent.Slices.SelectMany(x => x.Events());
            
            var ids = events.OfType<IEvent<TEvent>>().Select(identitySource).ToArray();
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
    }

    /// <summary>
    /// If you have a parallel projected view (or document) that shares
    /// the same identity, this will look up and reference the matching T
    /// for each active event slice
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public async Task ReferencePeerView<T>()
    {
        if (Operations == null)
        {
            throw new InvalidOperationException("This method can only be used within projection execution");
        }

        var cache = await loadPeerEntities<T>(Operations);
        foreach (var slice in Slices)
        {
            if (cache.TryFind(slice.Id, out var item))
            {
                slice.Reference(item);
            }
        }
    }

    private async Task<IAggregateCache<TId, T>> loadPeerEntities<T>(IStorageOperations session)
    {
        var cache = findCache<TId, T>();
        var storage = await session.FetchProjectionStorageAsync<T, TId>(TenantId, CancellationToken.None);

        var ids = Slices.Select(x => x.Id).ToArray();
        if (!ids.Any())
        {
            return new NulloAggregateCache<TId, T>();
        }
            
        if (cache == null)
        {
            var dict = await storage.LoadManyAsync(ids, CancellationToken.None);
            return new DictionaryAggregateCache<TId, T>(dict);
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
}
