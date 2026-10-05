using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// A projection that runs against the in-memory prototyping store (jasperfx#964).
/// </summary>
public interface IInMemoryProjection : IJasperFxProjection<IInMemoryDocumentSession>;

/// <summary>
/// The projections registered on the in-memory prototyping store (jasperfx#964), and the source of the
/// aggregators that live aggregation (<c>AggregateStreamAsync</c>, <c>FetchForWriting</c>,
/// <c>FetchLatest</c>) folds with.
/// </summary>
/// <remarks>
/// Live and Inline only: the async daemon is out of scope for a prototyping store, so an Async
/// registration is refused with a <see cref="NotSupportedException"/>. Inline projections run inside the
/// session's all-or-nothing commit, so a projection that throws rolls back the events and documents of
/// the same unit of work.
/// </remarks>
public class InMemoryProjectionGraph
    : ProjectionGraph<IInMemoryProjection, IInMemoryDocumentSession, IInMemoryQuerySession>
{
    private readonly InMemoryEventRegistry _events;
    private readonly object _freezeLock = new();
    private IInlineProjection<IInMemoryDocumentSession>[]? _inline;
    private int _frozenCount = -1;

    internal InMemoryProjectionGraph(InMemoryEventRegistry events) : base(events, "inmemory")
    {
        _events = events;
    }

    /// <inheritdoc />
    protected override void onAddProjection(object projection)
    {
        if (projection is ProjectionBase projectionBase)
        {
            foreach (var eventType in projectionBase.IncludedEventTypes)
            {
                _events.AddEventType(eventType);
            }
        }
    }

    /// <summary>
    /// Keep a self-aggregating document type up to date as a snapshot, folded from its stream's events.
    /// Only <see cref="SnapshotLifecycle.Inline"/> is supported: the snapshot is written in the same
    /// commit as the events.
    /// </summary>
    public void Snapshot<T>(SnapshotLifecycle lifecycle) where T : notnull
    {
        if (lifecycle != SnapshotLifecycle.Inline) throw asyncNotSupported(typeof(T).Name);

        if (typeof(T).CanBeCastTo<ProjectionBase>())
        {
            throw new InvalidOperationException(
                "Snapshot<T> is for self-aggregating document types. Register a projection class with " +
                $"Add() instead of {typeof(T).FullNameInCode()}.");
        }

        // Closed over the aggregate's own identity type, the same rule live aggregation follows, or the
        // source-generated evolver for its Apply / Create methods does not match.
        var projection = InMemoryAggregateIdentity.CreateLiveProjection<T>(
            InMemoryAggregateIdentity.ResolveIdType(typeof(T), _events.StreamIdentity));

        Add(projection, ProjectionLifecycle.Inline);
    }

    /// <summary>
    /// Declare a self-aggregating type for live aggregation. Nothing has to be registered for live
    /// aggregation on this store -- an aggregator is built the first time a type is folded -- so this only
    /// builds it now, and fails now rather than at the first fold if the type cannot be aggregated.
    /// </summary>
    public void LiveStreamAggregation<T>() where T : class => AggregatorFor<T>();

    /// <summary>
    /// Register a projection class: a <see cref="SingleStreamProjection{TDoc,TId}"/>,
    /// <see cref="MultiStreamProjection{TDoc,TId}"/> or <see cref="EventProjection"/>, or an
    /// <see cref="IInMemoryProjection"/>. Only <see cref="ProjectionLifecycle.Inline"/> and
    /// <see cref="ProjectionLifecycle.Live"/> are supported.
    /// </summary>
    public void Add(ProjectionBase projection, ProjectionLifecycle lifecycle)
    {
        if (lifecycle == ProjectionLifecycle.Async) throw asyncNotSupported(projection.GetType().Name);

        projection.Lifecycle = lifecycle;
        projection.AssembleAndAssertValidity();

        switch (projection)
        {
            case IProjectionSource<IInMemoryDocumentSession, IInMemoryQuerySession> source:
                Add(source, lifecycle);
                break;
            case IInMemoryProjection bare:
                Add(bare, lifecycle);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(projection),
                    $"'{projection.GetType().Name}' is not a projection for the in-memory prototyping store. Derive " +
                    "from one of the base classes in JasperFx.Events.InMemory.Projections, or implement " +
                    "IInMemoryProjection.");
        }
    }

    /// <summary>
    /// The inline projections every commit runs, frozen on first use: the registrations are validated
    /// once, and their inline runners built once.
    /// </summary>
    internal IReadOnlyList<IInlineProjection<IInMemoryDocumentSession>> InlineProjections(InMemoryDocumentStore store)
    {
        var inline = _inline;
        if (inline is not null && _frozenCount == All.Count) return inline;

        lock (_freezeLock)
        {
            if (_inline is not null && _frozenCount == All.Count) return _inline;

            // The base class's own Add overloads cannot refuse an Async lifecycle, so it is checked here too
            var async = All.FirstOrDefault(x => x.Lifecycle == ProjectionLifecycle.Async);
            if (async is not null) throw asyncNotSupported(async.Name);

            AssertValidity(store);

            _inline = All.Where(x => x.Lifecycle == ProjectionLifecycle.Inline)
                .Select(x => x.BuildForInline())
                .ToArray();
            _frozenCount = All.Count;

            return _inline;
        }
    }

    private static NotSupportedException asyncNotSupported(string name)
        => new($"'{name}' cannot run asynchronously: the in-memory prototyping store (AddInMemoryStoreForPrototyping) " +
               "has no async projection daemon, so only Inline and Live projections are supported. Switch to Marten, " +
               "Polecat or Fisher when you need async projections.");
}
