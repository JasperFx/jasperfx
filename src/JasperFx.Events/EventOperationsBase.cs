using System.Runtime.CompilerServices;
using JasperFx.Core;

namespace JasperFx.Events;

/// <summary>
///     The overload surface of <see cref="IEventOperations" /> and the write half of
///     <see cref="IEventStoreOperations" />, written once: every <c>StartStream</c>, <c>Append</c>,
///     <c>AppendOptimistic</c>, <c>AppendExclusive</c>, <c>FetchForWriting</c>,
///     <c>FetchForExclusiveWriting</c>, <c>WriteToAggregate</c> and <c>WriteExclusivelyToAggregate</c>.
///     A store supplies the seams and keeps everything that touches its session or its database.
/// </summary>
/// <remarks>
///     <para>
///     <b>Argument validation lives here and nowhere else (jasperfx#1047)</b>, because that is where the
///     stores' "identical" overloads turned out not to be. A Guid overload on a string-keyed store, or
///     the reverse, is an <see cref="InvalidOperationException" /> from every member, and an empty id is
///     an <see cref="ArgumentOutOfRangeException" /> from every member, before any seam runs and so
///     before any round trip. Marten checked both on <c>Append</c> and <c>StartStream</c>, Polecat
///     checked no identity at all, and Fisher checked no empty id.
///     </para>
///     <para>
///     <b>Nothing here reads a tenant, on purpose.</b> The tenant of a stream is decided by the
///     operations the call is made on (jasperfx#1044): a <c>ForTenant</c> scope is another instance over
///     the same unit of work, Marten's a nested session and Polecat's and Fisher's an instance carrying
///     its own tenant id. So stamping <see cref="StreamAction.TenantId" />, and scoping the version read
///     and the fetch, belong to the seams, which each run on the instance that was called. A base that
///     looked at a session field would see the parent's tenant under <c>ForTenant</c>.
///     </para>
///     <para>
///     <b>Nor does anything here assume how events reach the database.</b> Whether a stream is folded
///     or read from a document, whether it takes a row lock or none, and whether it is written by
///     Marten's rich or quick append are all behind the seams. This class only ever touches
///     <see cref="StreamAction.ExpectedVersionOnServer" />, which every append path already reads.
///     </para>
///     <para>
///     <b>A store with a base class of its own cannot derive from this</b>, and Marten's
///     <c>EventStore</c> and Polecat's <c>EventOperations</c> both derive from their
///     <c>QueryEventStore</c>. Those two host an instance through <see cref="IEventOperationsHost" />.
///     </para>
///     <para>
///     The surface stops at the members all stores agree on. The natural key overloads
///     <c>FetchForWriting&lt;T, TId&gt;</c> and <c>FetchForExclusiveWriting&lt;T, TId&gt;</c> stay per
///     store, because that is where they differ in substance: a fetch plan in Marten, a one-statement
///     locking planner in Polecat, a lookup then an ordinary fetch in Fisher.
///     </para>
/// </remarks>
public abstract class EventOperationsBase : IEventOperations
{
    /// <summary>
    ///     How this store identifies streams, which every overload is checked against.
    /// </summary>
    protected abstract StreamIdentity StreamIdentity { get; }

    /// <summary>
    ///     Register a new stream in the unit of work and hand back its action, with
    ///     <see cref="StreamAction.TenantId" /> stamped from this instance.
    /// </summary>
    /// <param name="aggregateType">
    ///     The aggregate type the stream was declared with, or null from an overload that declares none.
    ///     This is how a store enforces a mandatory declaration, as Marten's
    ///     <c>UseMandatoryStreamTypeDeclaration</c> does: refuse null here, and only the undeclared
    ///     overloads fail.
    /// </param>
    /// <param name="id">The stream id, already checked and never empty.</param>
    /// <param name="events">The events, already checked and never null or empty.</param>
    protected abstract StreamAction StartStreamCore(Type? aggregateType, Guid id, object[] events);

    /// <inheritdoc cref="StartStreamCore(Type, Guid, object[])" />
    protected abstract StreamAction StartStreamCore(Type? aggregateType, string streamKey, object[] events);

    /// <summary>
    ///     Add events to the stream's pending action in the unit of work, or begin one stamped with the
    ///     tenant of this instance.
    /// </summary>
    /// <param name="stream">The stream id, already checked and never empty.</param>
    /// <param name="events">The events, already checked and never null, but possibly empty.</param>
    protected abstract StreamAction AppendCore(Guid stream, object[] events);

    /// <inheritdoc cref="AppendCore(Guid, object[])" />
    protected abstract StreamAction AppendCore(string stream, object[] events);

    /// <summary>
    ///     Read the current version of a stream that has to exist, in the tenant of this instance, for
    ///     <c>AppendOptimistic</c> and <c>AppendExclusive</c>.
    /// </summary>
    /// <remarks>
    ///     A store with row locks begins its transaction and takes the lock when
    ///     <paramref name="exclusive" /> is set, and turns a lock failure into its
    ///     <see cref="StreamLockedException" />. A store with none, like Fisher, ignores the flag and
    ///     <c>AppendExclusive</c> is <c>AppendOptimistic</c> there.
    /// </remarks>
    /// <typeparam name="TId"><see cref="Guid" /> or <see cref="string" />.</typeparam>
    /// <exception cref="NonExistentStreamException">The stream does not exist.</exception>
    protected abstract Task<long> ReadStreamVersionCore<TId>(TId stream, bool exclusive, CancellationToken token)
        where TId : notnull;

    /// <summary>
    ///     The store's own <c>FetchForWriting</c>: its concurrency approach, its folding, its tracking.
    /// </summary>
    /// <param name="id">The stream identity, already checked and never empty.</param>
    /// <param name="expectedVersion">
    ///     The version the caller says the stream is at, or null. Never set together with
    ///     <paramref name="exclusive" />, because the contract has no such overload.
    /// </param>
    /// <param name="exclusive">Set by <c>FetchForExclusiveWriting</c>.</param>
    /// <param name="cancellation">Carried into the returned stream.</param>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <typeparam name="TId"><see cref="Guid" /> or <see cref="string" />.</typeparam>
    protected abstract Task<IEventStream<T>> FetchForWritingCore<T, TId>(TId id, long? expectedVersion, bool exclusive,
        CancellationToken cancellation) where T : class where TId : notnull;

    /// <summary>
    ///     Commit the unit of work this instance writes to, which is how <c>WriteToAggregate</c> ends.
    /// </summary>
    protected abstract Task SaveChangesCore(CancellationToken cancellation);

    /// <summary>
    ///     Turn the version a caller passed to <c>Append(stream, expectedVersion, ...)</c> into
    ///     <see cref="StreamAction.ExpectedVersionOnServer" />, called once the events are on the action.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>This is a virtual because the stores really do mean different things by the argument
    ///     (jasperfx#1047).</b> Marten and Polecat read it as the version of the stream <em>after</em> the
    ///     append, so <c>Append(id, 2, e)</c> succeeds on a stream at version 1, and this default subtracts
    ///     the pending events. Fisher reads it as the version <em>before</em> the append, so the same call
    ///     succeeds on a stream at version 2, and it pins that with a test that appending one event at
    ///     expected version 0 creates a stream. A store on that reading overrides this to return
    ///     <paramref name="expectedVersion" />.
    ///     </para>
    ///     <para>
    ///     The two overloads share this one guard. Marten's Guid overload refused a version below the
    ///     pending event count, and its string overload let a negative expected version through to the
    ///     database.
    ///     </para>
    /// </remarks>
    /// <param name="expectedVersion">The caller's expected version.</param>
    /// <param name="stream">The stream action, with the events of this call already added.</param>
    protected virtual long ExpectedStartingVersion(long expectedVersion, StreamAction stream)
    {
        var starting = expectedVersion - stream.Events.Count;

        if (starting < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                "The expected version cannot be less than the number of events being appended");
        }

        return starting;
    }

    // ---- StartStream ----

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => startStream(typeof(TAggregate), id, events);

    /// <inheritdoc cref="IEventStoreOperations.StartStream{TAggregate}(Guid, IEnumerable{object})" />
    public StreamAction StartStream<TAggregate>(Guid id, IEnumerable<object> events) where TAggregate : class
        => startStream(typeof(TAggregate), id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, Guid id, IEnumerable<object> events)
        => startStream(aggregateType, id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, Guid id, params object[] events)
        => startStream(aggregateType, id, events);

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(string streamKey, IEnumerable<object> events) where TAggregate : class
        => startStream(typeof(TAggregate), streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(string streamKey, params object[] events) where TAggregate : class
        => startStream(typeof(TAggregate), streamKey, events);

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, string streamKey, IEnumerable<object> events)
        => startStream(aggregateType, streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, string streamKey, params object[] events)
        => startStream(aggregateType, streamKey, events);

    /// <inheritdoc />
    public StreamAction StartStream(Guid id, IEnumerable<object> events) => startStream(null, id, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Guid id, params object[] events) => startStream(null, id, events);

    /// <inheritdoc />
    public StreamAction StartStream(string streamKey, IEnumerable<object> events)
        => startStream(null, streamKey, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(string streamKey, params object[] events) => startStream(null, streamKey, events);

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(IEnumerable<object> events) where TAggregate : class
        => startNewStream(typeof(TAggregate), events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream<TAggregate>(params object[] events) where TAggregate : class
        => startNewStream(typeof(TAggregate), events);

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, IEnumerable<object> events)
        => startNewStream(aggregateType, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(Type aggregateType, params object[] events)
        => startNewStream(aggregateType, events);

    /// <inheritdoc />
    public StreamAction StartStream(IEnumerable<object> events) => startNewStream(null, events.ToArray());

    /// <inheritdoc />
    public StreamAction StartStream(params object[] events) => startNewStream(null, events);

    // ---- Append ----

    /// <inheritdoc />
    public StreamAction Append(Guid stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(Guid stream, params object[] events)
    {
        assertStream(stream);
        ArgumentNullException.ThrowIfNull(events);

        return AppendCore(stream, events);
    }

    /// <inheritdoc />
    public StreamAction Append(string stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(string stream, params object[] events)
    {
        assertStream(stream);
        ArgumentNullException.ThrowIfNull(events);

        return AppendCore(stream, events);
    }

    /// <inheritdoc cref="IEventStoreOperations.Append(Guid, long, IEnumerable{object})" />
    public StreamAction Append(Guid stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(Guid stream, long expectedVersion, params object[] events)
        => expecting(Append(stream, events), expectedVersion);

    /// <inheritdoc />
    public StreamAction Append(string stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

    /// <inheritdoc />
    public StreamAction Append(string stream, long expectedVersion, params object[] events)
        => expecting(Append(stream, events), expectedVersion);

    // ---- AppendOptimistic ----

    /// <inheritdoc cref="IEventStoreOperations.AppendOptimistic(Guid, CancellationToken, object[])" />
    public async Task AppendOptimistic(Guid streamId, CancellationToken token, params object[] events)
    {
        assertStream(streamId);
        var version = await ReadStreamVersionCore(streamId, false, token).ConfigureAwait(false);
        Append(streamId, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="IEventStoreOperations.AppendOptimistic(Guid, object[])" />
    public Task AppendOptimistic(Guid streamId, params object[] events)
        => AppendOptimistic(streamId, CancellationToken.None, events);

    /// <inheritdoc cref="IEventStoreOperations.AppendOptimistic(string, CancellationToken, object[])" />
    public async Task AppendOptimistic(string streamKey, CancellationToken token, params object[] events)
    {
        assertStream(streamKey);
        var version = await ReadStreamVersionCore(streamKey, false, token).ConfigureAwait(false);
        Append(streamKey, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="IEventStoreOperations.AppendOptimistic(string, object[])" />
    public Task AppendOptimistic(string streamKey, params object[] events)
        => AppendOptimistic(streamKey, CancellationToken.None, events);

    // ---- AppendExclusive ----

    /// <inheritdoc cref="IEventStoreOperations.AppendExclusive(Guid, CancellationToken, object[])" />
    public async Task AppendExclusive(Guid streamId, CancellationToken token, params object[] events)
    {
        assertStream(streamId);
        var version = await ReadStreamVersionCore(streamId, true, token).ConfigureAwait(false);
        Append(streamId, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="IEventStoreOperations.AppendExclusive(Guid, object[])" />
    public Task AppendExclusive(Guid streamId, params object[] events)
        => AppendExclusive(streamId, CancellationToken.None, events);

    /// <inheritdoc cref="IEventStoreOperations.AppendExclusive(string, CancellationToken, object[])" />
    public async Task AppendExclusive(string streamKey, CancellationToken token, params object[] events)
    {
        assertStream(streamKey);
        var version = await ReadStreamVersionCore(streamKey, true, token).ConfigureAwait(false);
        Append(streamKey, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="IEventStoreOperations.AppendExclusive(string, object[])" />
    public Task AppendExclusive(string streamKey, params object[] events)
        => AppendExclusive(streamKey, CancellationToken.None, events);

    // ---- FetchForWriting ----
    //
    // async rather than handing back the seam's Task, so that a refused identity is a faulted Task from
    // here as it always was from Marten, and not a synchronous throw from one member of the family.

    /// <inheritdoc cref="IEventStoreOperations.FetchForWriting{T}(Guid, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class
    {
        assertStream(id);
        return await FetchForWritingCore<T, Guid>(id, null, false, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEventStoreOperations.FetchForWriting{T}(string, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForWriting<T>(string key, CancellationToken cancellation = default)
        where T : class
    {
        assertStream(key);
        return await FetchForWritingCore<T, string>(key, null, false, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEventStoreOperations.FetchForWriting{T}(Guid, long, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion,
        CancellationToken cancellation = default) where T : class
    {
        assertStream(id);
        return await FetchForWritingCore<T, Guid>(id, expectedVersion, false, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEventStoreOperations.FetchForWriting{T}(string, long, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion,
        CancellationToken cancellation = default) where T : class
    {
        assertStream(key);
        return await FetchForWritingCore<T, string>(key, expectedVersion, false, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEventStoreOperations.FetchForExclusiveWriting{T}(Guid, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForExclusiveWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class
    {
        assertStream(id);
        return await FetchForWritingCore<T, Guid>(id, null, true, cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEventStoreOperations.FetchForExclusiveWriting{T}(string, CancellationToken)" />
    public async Task<IEventStream<T>> FetchForExclusiveWriting<T>(string key, CancellationToken cancellation = default)
        where T : class
    {
        assertStream(key);
        return await FetchForWritingCore<T, string>(key, null, true, cancellation).ConfigureAwait(false);
    }

    // ---- WriteToAggregate ----

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(Guid, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing, CancellationToken cancellation = default)
        where T : class
        => write(FetchForWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(Guid, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(string, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(string, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(Guid, int, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, expectedVersion, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(Guid, int, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, expectedVersion, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(string, int, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, expectedVersion, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteToAggregate{T}(string, int, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForWriting<T>(id, expectedVersion, cancellation), writing, cancellation);

    // ---- WriteExclusivelyToAggregate ----

    /// <inheritdoc cref="IEventStoreOperations.WriteExclusivelyToAggregate{T}(Guid, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForExclusiveWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteExclusivelyToAggregate{T}(string, Action{IEventStream{T}}, CancellationToken)" />
    public Task WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForExclusiveWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteExclusivelyToAggregate{T}(Guid, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForExclusiveWriting<T>(id, cancellation), writing, cancellation);

    /// <inheritdoc cref="IEventStoreOperations.WriteExclusivelyToAggregate{T}(string, Func{IEventStream{T}, Task}, CancellationToken)" />
    public Task WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => write(FetchForExclusiveWriting<T>(id, cancellation), writing, cancellation);

    // ---- internals ----

    private StreamAction startStream(Type? aggregateType, Guid id, object[] events)
    {
        assertStream(id);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Length == 0)
        {
            throw new EmptyEventStreamException(id);
        }

        return StartStreamCore(aggregateType, id, events);
    }

    private StreamAction startStream(Type? aggregateType, string streamKey, object[] events)
    {
        assertStream(streamKey);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Length == 0)
        {
            throw new EmptyEventStreamException(streamKey);
        }

        return StartStreamCore(aggregateType, streamKey, events);
    }

    /// <summary>
    ///     Start a stream under an identity made up here.
    /// </summary>
    /// <remarks>
    ///     By the store's identity rather than always a Guid: Marten's overloads refused on a string-keyed
    ///     store, and Polecat's built an action with no key at all, where Fisher's made up a key.
    /// </remarks>
    private StreamAction startNewStream(Type? aggregateType, object[] events)
        => StreamIdentity == StreamIdentity.AsGuid
            ? startStream(aggregateType, CombGuidIdGeneration.NewGuid(), events)
            : startStream(aggregateType, Guid.NewGuid().ToString(), events);

    private StreamAction expecting(StreamAction stream, long expectedVersion)
    {
        stream.ExpectedVersionOnServer = ExpectedStartingVersion(expectedVersion, stream);
        return stream;
    }

    private async Task write<T>(Task<IEventStream<T>> fetching, Action<IEventStream<T>> writing,
        CancellationToken cancellation) where T : class
    {
        writing(await fetching.ConfigureAwait(false));
        await SaveChangesCore(cancellation).ConfigureAwait(false);
    }

    private async Task write<T>(Task<IEventStream<T>> fetching, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation) where T : class
    {
        await writing(await fetching.ConfigureAwait(false)).ConfigureAwait(false);
        await SaveChangesCore(cancellation).ConfigureAwait(false);
    }

    private void assertStream(Guid id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
    {
        if (StreamIdentity == StreamIdentity.AsString)
        {
            throw new InvalidOperationException(
                "This event store is configured for string stream identity (opts.Events.StreamIdentity = StreamIdentity.AsString). Use the string stream key overloads, or set opts.Events.StreamIdentity = StreamIdentity.AsGuid if streams should be keyed by Guid.");
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(paramName, "Cannot use an empty Guid as the stream id");
        }
    }

    private void assertStream(string key, [CallerArgumentExpression(nameof(key))] string? paramName = null)
    {
        if (StreamIdentity == StreamIdentity.AsGuid)
        {
            throw new InvalidOperationException(
                "This event store is configured for Guid stream identity (opts.Events.StreamIdentity = StreamIdentity.AsGuid). Use the Guid stream id overloads, or set opts.Events.StreamIdentity = StreamIdentity.AsString if streams should be keyed by string.");
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentOutOfRangeException(paramName, "The stream key cannot be null or empty");
        }
    }
}
