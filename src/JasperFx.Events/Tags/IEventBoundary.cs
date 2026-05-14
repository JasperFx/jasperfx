namespace JasperFx.Events.Tags;

/// <summary>
///     Writable handle returned by
///     <see cref="IEventStoreOperations.FetchForWritingByTags{T}(EventTagQuery, CancellationToken)"/>
///     for Dynamic Consistency Boundary (DCB) workflows. Unlike
///     <see cref="IEventStream{T}"/>, which is pinned to a single stream, an
///     <see cref="IEventBoundary{T}"/> spans every stream whose events match
///     the boundary's tag query: the events are aggregated into
///     <typeparamref name="T"/>, additional events can be appended via
///     <see cref="AppendOne"/> / <see cref="AppendMany(object[])"/>, and the
///     consuming product asserts at <c>SaveChangesAsync()</c> time that no
///     new events matching the tag query have been written since
///     <see cref="LastSeenSequence"/>.
///     <para>
///     Lifted from Marten's <c>Marten.Events.Dcb.IEventBoundary&lt;T&gt;</c>
///     and Polecat's <c>Polecat.Events.Dcb.IEventBoundary&lt;T&gt;</c> into
///     <c>JasperFx.Events.Tags</c> per the dedupe pillar
///     (<see href="https://github.com/JasperFx/jasperfx/issues/214"/>). Each
///     product retains its own concrete <c>EventBoundary&lt;T&gt;</c> behind
///     this contract — only the shape is shared.
///     </para>
/// </summary>
/// <typeparam name="T">
///     The aggregate type built from the events matching the tag query.
///     Constrained to <c>class</c> to match the rest of the lifted
///     aggregate-handler surface (<see cref="IEventStream{T}"/> et al.).
/// </typeparam>
public interface IEventBoundary<out T> where T : class
{
    /// <summary>
    ///     The current aggregate state built from the events matching the
    ///     tag query. Null when no matching events were found.
    /// </summary>
    T? Aggregate { get; }

    /// <summary>
    ///     The highest event sequence number observed when the boundary was
    ///     established. The consuming product uses this as the consistency
    ///     marker — if any event matching the tag query has been appended
    ///     past <see cref="LastSeenSequence"/> by the time the boundary is
    ///     committed, the save fails with a DCB concurrency exception.
    /// </summary>
    long LastSeenSequence { get; }

    /// <summary>
    ///     The events loaded by the tag query, ordered by sequence.
    /// </summary>
    IReadOnlyList<IEvent> Events { get; }

    /// <summary>
    ///     Append a single event to the boundary. The event must carry tags
    ///     (set explicitly via <c>WithTag()</c> or inferred from public
    ///     properties at append time) so the product-specific compactor can
    ///     route it to the appropriate stream.
    /// </summary>
    void AppendOne(object @event);

    /// <summary>
    ///     Append multiple events to the boundary. Each event must carry
    ///     tags — see <see cref="AppendOne"/>.
    /// </summary>
    void AppendMany(params object[] events);

    /// <summary>
    ///     Append multiple events to the boundary. Each event must carry
    ///     tags — see <see cref="AppendOne"/>.
    /// </summary>
    void AppendMany(IEnumerable<object> events);
}
