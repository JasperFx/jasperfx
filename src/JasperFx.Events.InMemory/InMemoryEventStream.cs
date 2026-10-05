namespace JasperFx.Events.InMemory;

/// <summary>
/// The <see cref="IEventStream{T}"/> <c>FetchForWriting</c> hands back on the in-memory prototyping store
/// (jasperfx#964): the folded aggregate plus a stream action tracked on the session, so appends ride the
/// session's commit and are guarded by the version the aggregate was folded at.
/// </summary>
internal sealed class InMemoryEventStream<T> : IEventStream<T> where T : class
{
    private readonly InMemoryEventOperations _operations;
    private StreamAction _stream;

    internal InMemoryEventStream(InMemoryEventOperations operations, StreamAction stream, T? aggregate,
        CancellationToken cancellation)
    {
        _operations = operations;
        _stream = stream;
        _stream.AggregateType ??= typeof(T);
        Aggregate = aggregate;
        Cancellation = cancellation;
    }

    public T? Aggregate { get; }

    public Guid Id => _stream.Id;

    public string? Key => _stream.Key;

    public long? StartingVersion => _stream.ExpectedVersionOnServer;

    public long? CurrentVersion => _stream.ExpectedVersionOnServer is null
        ? null
        : _stream.ExpectedVersionOnServer.Value + _stream.Events.Count;

    public CancellationToken Cancellation { get; }

    public IReadOnlyList<IEvent> Events => _stream.Events;

    public bool AlwaysEnforceConsistency
    {
        get => _stream.AlwaysEnforceConsistency;
        set => _stream.AlwaysEnforceConsistency = value;
    }

    public void AppendOne(object @event) => _stream.AddEvent(_operations.BuildEvent(@event));

    public void AppendMany(params object[] events) => AppendMany((IEnumerable<object>)events);

    public void AppendMany(IEnumerable<object> events)
        => _stream.AddEvents(events.Select(_operations.BuildEvent).ToArray());

    /// <summary>
    /// After a commit the session stops tracking this stream; fast-forward it to the committed version and
    /// track it again, so a second round of appends on the same stream object guards the right version.
    /// </summary>
    public void TryFastForwardVersion()
    {
        if (_operations.IsTracking(_stream)) return;

        _stream = _stream.FastForward();
        _operations.TrackFetched(_stream);
    }
}
