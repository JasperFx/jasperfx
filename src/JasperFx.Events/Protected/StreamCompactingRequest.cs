using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Protected;

/// <summary>
///     Marker base for all <see cref="IEventsArchiver{TOperations}"/> closures
///     so the (operation-agnostic) <see cref="StreamCompactingRequest{T}.Archiver"/>
///     property can be typed against a single interface that any product can
///     downcast at execution time. Implementations should always implement the
///     closed-generic <see cref="IEventsArchiver{TOperations}"/> — this marker
///     exists only to keep <see cref="StreamCompactingRequest{T}"/> from having
///     to flow a <c>TOperations</c> type parameter through every API that
///     touches a compacting request.
/// </summary>
public interface IEventsArchiver
{
}

/// <summary>
///     Callback interface for executing event archiving before stream compaction.
///     Parameterized on the consuming product's <c>IDocumentOperations</c> type
///     so the lifted shape works for both Marten and Polecat (each closes the
///     generic against its own operations type at the registration boundary).
/// </summary>
/// <typeparam name="TOperations">
///     The product's document-operations type (e.g.
///     <c>Marten.IDocumentOperations</c> or <c>Polecat.IDocumentOperations</c>).
/// </typeparam>
public interface IEventsArchiver<TOperations> : IEventsArchiver
{
    /// <summary>
    ///     Invoked once the compactor has gathered the events that are about to
    ///     be replaced by the <see cref="Compacted{T}"/> snapshot, but before
    ///     the compactor writes the replacement event. Implementations can use
    ///     this hook to copy the to-be-deleted events into cold storage, emit
    ///     audit records, etc. The compactor will not proceed until this
    ///     callback completes.
    /// </summary>
    Task MaybeArchiveAsync<T>(TOperations operations, StreamCompactingRequest<T> request,
        IReadOnlyList<IEvent> events, CancellationToken cancellation) where T : class;
}

/// <summary>
///     Data-carrier describing a stream-compaction request: which stream to
///     compact, optionally up to which version or timestamp, and an optional
///     <see cref="IEventsArchiver{TOperations}"/> hook for archiving the
///     events that will be replaced. The compactor (product-specific) reads
///     this, fetches the events, builds a <see cref="Compacted{T}"/> snapshot
///     of <typeparamref name="T"/>, replaces the last event with that
///     snapshot, deletes the rest, and (if supplied) calls the archiver
///     before the destructive step.
///     <para>
///     Lifted to <c>JasperFx.Events.Protected</c> from
///     <c>Polecat.Events.Protected</c> per the dedupe pillar
///     (<see href="https://github.com/JasperFx/jasperfx/issues/214"/>) so
///     Marten and Polecat can share the same contract. Each product retains
///     its own <c>CompactStreamAsync</c> extension / instance method which
///     drives the execution against its session/operations type — only the
///     data shape is shared.
///     </para>
/// </summary>
/// <typeparam name="T">
///     The aggregate type the stream compacts into. Must be a reference type
///     (the aggregator graph builds an instance of <typeparamref name="T"/>
///     from the event stream and packages it inside <see cref="Compacted{T}"/>).
/// </typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: holds a reference to an aggregator-graph-built T that the compactor packs into a Compacted<T>. Aggregate type T flows in from caller code and is preserved by projection registration on the caller side per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: holds a generic type-argument that the consuming compactor uses with the aggregator graph (Type.MakeGenericType + FastExpressionCompiler). AOT consumers rely on the source-generated aggregator helpers from JasperFx.Events.SourceGenerator.")]
public class StreamCompactingRequest<T> where T : class
{
    /// <summary>
    ///     Create a compacting request scoped to a string-identified stream.
    /// </summary>
    public StreamCompactingRequest(string? streamKey)
    {
        StreamKey = streamKey;
    }

    /// <summary>
    ///     Create a compacting request scoped to a Guid-identified stream.
    /// </summary>
    public StreamCompactingRequest(Guid? streamId)
    {
        StreamId = streamId;
    }

    /// <summary>
    ///     The identity of the stream if using string-identified streams.
    /// </summary>
    public string? StreamKey { get; private set; }

    /// <summary>
    ///     The identity of the stream if using Guid-identified streams.
    /// </summary>
    public Guid? StreamId { get; private set; }

    /// <summary>
    ///     If specified, the version at which the stream is going to be compacted.
    ///     Default 0 means the latest.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    ///     If specified, this operation will compact the events below the timestamp.
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    ///     Optional mechanism to carry out an archiving step for the events before the
    ///     compacting operation is completed and these events are permanently deleted.
    ///     Closed over the consuming product's <c>IDocumentOperations</c> type via
    ///     <see cref="IEventsArchiver{TOperations}"/>; consumers typically store an
    ///     instance of their product-specific closed-generic archiver here and
    ///     downcast back to <c>IEventsArchiver&lt;TOperations&gt;</c> at execution
    ///     time inside their product-specific compactor.
    /// </summary>
    public IEventsArchiver? Archiver { get; set; }

    /// <summary>
    ///     CancellationToken for just this operation. Default is None.
    /// </summary>
    public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    /// <summary>
    ///     The event sequence of the last event being compacted. Set by the
    ///     compactor before invoking <see cref="IEventsArchiver{TOperations}.MaybeArchiveAsync{T}"/>
    ///     so the archiver knows the high-water mark of the events being
    ///     consumed.
    /// </summary>
    public long Sequence { get; set; }
}
