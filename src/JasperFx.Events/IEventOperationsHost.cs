namespace JasperFx.Events;

/// <summary>
///     Implement <see cref="IEventStoreOperations" />'s write surface by forwarding it to an
///     <see cref="EventOperationsBase" />, for a store class that cannot derive from one because it
///     already has a base class.
/// </summary>
/// <remarks>
///     <para>
///     Marten's <c>EventStore</c> and Polecat's <c>EventOperations</c> both derive from their
///     <c>QueryEventStore</c>, which read-only sessions also use, and C# allows the one base class
///     (jasperfx#1047). So such a store keeps that base, adds this interface, and puts its seams on a
///     small nested subclass of <see cref="EventOperationsBase" />. Fisher, whose
///     <c>EventOperations</c> has no base class and is what <c>session.Events</c> returns, derives from
///     <see cref="EventOperationsBase" /> directly.
///     </para>
///     <para>
///     <b>These members are only visible through an interface</b>, so this route suits a store whose
///     session exposes its event operations as an interface, and calls from inside the class itself
///     have to go through <see cref="Operations" />. <see cref="EventOperationsBase" /> stays the one
///     copy of the logic: everything here forwards and nothing here decides anything.
///     </para>
///     <para>
///     What is left abstract is what a store implements today anyway: reads, archiving, the natural key
///     overloads of <c>FetchForWriting</c>, and the rest of <see cref="IEventStoreOperations" />.
///     </para>
/// </remarks>
public interface IEventOperationsHost : IEventStoreOperations
{
    /// <summary>
    ///     The operations every write member forwards to. It must be scoped to the same tenant as the
    ///     instance holding it, because the tenant is decided by the operations a call is made on.
    /// </summary>
    EventOperationsBase Operations { get; }

    // ---- StartStream ----

    StreamAction IEventOperations.StartStream<TAggregate>(Guid id, params object[] events)
        => Operations.StartStream<TAggregate>(id, events);

    StreamAction IEventStoreOperations.StartStream<TAggregate>(Guid id, IEnumerable<object> events)
        => Operations.StartStream<TAggregate>(id, events);

    StreamAction IEventOperations.StartStream(Type aggregateType, Guid id, IEnumerable<object> events)
        => Operations.StartStream(aggregateType, id, events);

    StreamAction IEventOperations.StartStream(Type aggregateType, Guid id, params object[] events)
        => Operations.StartStream(aggregateType, id, events);

    StreamAction IEventOperations.StartStream<TAggregate>(string streamKey, IEnumerable<object> events)
        => Operations.StartStream<TAggregate>(streamKey, events);

    StreamAction IEventOperations.StartStream<TAggregate>(string streamKey, params object[] events)
        => Operations.StartStream<TAggregate>(streamKey, events);

    StreamAction IEventOperations.StartStream(Type aggregateType, string streamKey, IEnumerable<object> events)
        => Operations.StartStream(aggregateType, streamKey, events);

    StreamAction IEventOperations.StartStream(Type aggregateType, string streamKey, params object[] events)
        => Operations.StartStream(aggregateType, streamKey, events);

    StreamAction IEventOperations.StartStream(Guid id, IEnumerable<object> events)
        => Operations.StartStream(id, events);

    StreamAction IEventOperations.StartStream(Guid id, params object[] events)
        => Operations.StartStream(id, events);

    StreamAction IEventOperations.StartStream(string streamKey, IEnumerable<object> events)
        => Operations.StartStream(streamKey, events);

    StreamAction IEventOperations.StartStream(string streamKey, params object[] events)
        => Operations.StartStream(streamKey, events);

    StreamAction IEventOperations.StartStream<TAggregate>(IEnumerable<object> events)
        => Operations.StartStream<TAggregate>(events);

    StreamAction IEventOperations.StartStream<TAggregate>(params object[] events)
        => Operations.StartStream<TAggregate>(events);

    StreamAction IEventOperations.StartStream(Type aggregateType, IEnumerable<object> events)
        => Operations.StartStream(aggregateType, events);

    StreamAction IEventOperations.StartStream(Type aggregateType, params object[] events)
        => Operations.StartStream(aggregateType, events);

    StreamAction IEventOperations.StartStream(IEnumerable<object> events)
        => Operations.StartStream(events);

    StreamAction IEventOperations.StartStream(params object[] events)
        => Operations.StartStream(events);

    // ---- Append ----

    StreamAction IEventOperations.Append(Guid stream, IEnumerable<object> events)
        => Operations.Append(stream, events);

    StreamAction IEventOperations.Append(Guid stream, params object[] events)
        => Operations.Append(stream, events);

    StreamAction IEventOperations.Append(string stream, IEnumerable<object> events)
        => Operations.Append(stream, events);

    StreamAction IEventOperations.Append(string stream, params object[] events)
        => Operations.Append(stream, events);

    StreamAction IEventStoreOperations.Append(Guid stream, long expectedVersion, IEnumerable<object> events)
        => Operations.Append(stream, expectedVersion, events);

    StreamAction IEventOperations.Append(Guid stream, long expectedVersion, params object[] events)
        => Operations.Append(stream, expectedVersion, events);

    StreamAction IEventOperations.Append(string stream, long expectedVersion, IEnumerable<object> events)
        => Operations.Append(stream, expectedVersion, events);

    StreamAction IEventOperations.Append(string stream, long expectedVersion, params object[] events)
        => Operations.Append(stream, expectedVersion, events);

    // ---- AppendOptimistic and AppendExclusive ----

    Task IEventStoreOperations.AppendOptimistic(Guid streamId, CancellationToken token, params object[] events)
        => Operations.AppendOptimistic(streamId, token, events);

    Task IEventStoreOperations.AppendOptimistic(Guid streamId, params object[] events)
        => Operations.AppendOptimistic(streamId, events);

    Task IEventStoreOperations.AppendOptimistic(string streamKey, CancellationToken token, params object[] events)
        => Operations.AppendOptimistic(streamKey, token, events);

    Task IEventStoreOperations.AppendOptimistic(string streamKey, params object[] events)
        => Operations.AppendOptimistic(streamKey, events);

    Task IEventStoreOperations.AppendExclusive(Guid streamId, CancellationToken token, params object[] events)
        => Operations.AppendExclusive(streamId, token, events);

    Task IEventStoreOperations.AppendExclusive(Guid streamId, params object[] events)
        => Operations.AppendExclusive(streamId, events);

    Task IEventStoreOperations.AppendExclusive(string streamKey, CancellationToken token, params object[] events)
        => Operations.AppendExclusive(streamKey, token, events);

    Task IEventStoreOperations.AppendExclusive(string streamKey, params object[] events)
        => Operations.AppendExclusive(streamKey, events);

    // ---- FetchForWriting ----

    Task<IEventStream<T>> IEventStoreOperations.FetchForWriting<T>(Guid id, CancellationToken cancellation)
        => Operations.FetchForWriting<T>(id, cancellation);

    Task<IEventStream<T>> IEventStoreOperations.FetchForWriting<T>(string key, CancellationToken cancellation)
        => Operations.FetchForWriting<T>(key, cancellation);

    Task<IEventStream<T>> IEventStoreOperations.FetchForWriting<T>(Guid id, long expectedVersion,
        CancellationToken cancellation)
        => Operations.FetchForWriting<T>(id, expectedVersion, cancellation);

    Task<IEventStream<T>> IEventStoreOperations.FetchForWriting<T>(string key, long expectedVersion,
        CancellationToken cancellation)
        => Operations.FetchForWriting<T>(key, expectedVersion, cancellation);

    Task<IEventStream<T>> IEventStoreOperations.FetchForExclusiveWriting<T>(Guid id, CancellationToken cancellation)
        => Operations.FetchForExclusiveWriting<T>(id, cancellation);

    Task<IEventStream<T>> IEventStoreOperations.FetchForExclusiveWriting<T>(string key, CancellationToken cancellation)
        => Operations.FetchForExclusiveWriting<T>(key, cancellation);

    // ---- WriteToAggregate ----

    Task IEventStoreOperations.WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, expectedVersion, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, expectedVersion, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, expectedVersion, writing, cancellation);

    Task IEventStoreOperations.WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteToAggregate(id, expectedVersion, writing, cancellation);

    // ---- WriteExclusivelyToAggregate ----

    Task IEventStoreOperations.WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteExclusivelyToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation)
        => Operations.WriteExclusivelyToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteExclusivelyToAggregate(id, writing, cancellation);

    Task IEventStoreOperations.WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation)
        => Operations.WriteExclusivelyToAggregate(id, writing, cancellation);
}
