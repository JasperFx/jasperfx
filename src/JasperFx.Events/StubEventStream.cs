namespace JasperFx.Events;

/// <summary>
/// A stand-in for <see cref="IEventStream{T}"/> for unit testing a handler that takes a stream
/// handle directly — Wolverine's <c>[WriteAggregate] IEventStream&lt;Account&gt; account</c>. Pass
/// one in, call the handler, assert on <see cref="EventsAppended"/>. No database, and no mocking
/// library (jasperfx#858).
/// </summary>
/// <remarks>
/// <para>
/// This is the preferred alternative to mocking <see cref="IEventStream{T}"/>. A verification of
/// <c>Received(1).AppendOne(…)</c> proves only that a method was called; a recorded list of events
/// is the thing the handler is actually supposed to produce, and reads the same whichever store is
/// behind the interface in production.
/// </para>
/// <para>
/// Lives here rather than in a store because <see cref="IEventStream{T}"/> does. The equivalent
/// stub has existed in Marten since long before this one, built on that store's own
/// <c>EventGraph</c>, which left Polecat and Fisher users to write their own or reach for a mock.
/// </para>
/// <para>
/// Every member the handler under test does not read is inert: appends are recorded rather than
/// persisted, and <see cref="TryFastForwardVersion"/> does nothing. The versions and identities are
/// settable so a version-sensitive or multi-stream handler can be exercised.
/// </para>
/// </remarks>
/// <typeparam name="T">The aggregate type.</typeparam>
public class StubEventStream<T> : IEventStream<T> where T : notnull
{
    private readonly IEventRegistry _registry;

    /// <summary>
    /// Start from an existing aggregate — or null for a stream that does not exist yet, which is
    /// what a handler that starts a stream should see.
    /// </summary>
    public StubEventStream(T? aggregate) : this(aggregate, new EventRegistry())
    {
    }

    /// <summary>
    /// Start from an existing aggregate and a registry. You only need this overload when the
    /// assertions care about the event type names on <see cref="Events"/> and the test has
    /// customized aliases — <see cref="EventsAppended"/> does not go through the registry at all.
    /// </summary>
    /// <param name="aggregate">The aggregate state the handler should see, or null.</param>
    /// <param name="registry">Supplies the event type naming used to wrap appended events.</param>
    public StubEventStream(T? aggregate, IEventRegistry registry)
    {
        Aggregate = aggregate;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Every event appended to this stream, in order — the raw event bodies the handler emitted,
    /// and what most assertions want.
    /// </summary>
    public List<object> EventsAppended { get; } = new();

    /// <inheritdoc />
    public T? Aggregate { get; }

    /// <inheritdoc cref="IEventStream{T}.StartingVersion" />
    public long? StartingVersion { get; set; }

    /// <inheritdoc cref="IEventStream{T}.CurrentVersion" />
    public long? CurrentVersion { get; set; }

    /// <inheritdoc cref="IEventStream{T}.Cancellation" />
    public CancellationToken Cancellation { get; set; }

    /// <summary>
    /// The Guid identity of the stream. Defaults to a new Guid so a handler can read it; set it to
    /// wire a command's id to the stream in a multi-stream test.
    /// </summary>
    /// <remarks>
    /// Both this and <see cref="Key"/> are populated by default, because the stub does not know
    /// which identity style the handler under test reads. Set whichever it does.
    /// </remarks>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The string key of the stream. See <see cref="Id"/> for why both are populated.</summary>
    public string? Key { get; set; } = Guid.NewGuid().ToString();

    /// <inheritdoc cref="IEventStream{T}.AlwaysEnforceConsistency" />
    public bool AlwaysEnforceConsistency { get; set; }

    /// <summary>
    /// <see cref="EventsAppended"/> wrapped in <see cref="IEvent"/> envelopes, for a handler or an
    /// assertion that reads the interface's own member. Built on each read.
    /// </summary>
    /// <remarks>
    /// The envelopes carry the event type naming and nothing a real store would only know at save
    /// time: no sequence, no version, no timestamp beyond the default. Assert on
    /// <see cref="EventsAppended"/> unless you specifically need the envelope.
    /// </remarks>
    public IReadOnlyList<IEvent> Events => EventsAppended.Select(x => _registry.BuildEvent(x)).ToList();

    /// <inheritdoc />
    public void AppendOne(object @event)
    {
        EventsAppended.Add(@event);
    }

    /// <inheritdoc />
    public void AppendMany(params object[] events)
    {
        EventsAppended.AddRange(events);
    }

    /// <inheritdoc />
    public void AppendMany(IEnumerable<object> events)
    {
        EventsAppended.AddRange(events);
    }

    /// <summary>
    /// No-op in the stub. There is no optimistic concurrency check to fast-forward.
    /// </summary>
    public void TryFastForwardVersion()
    {
        // Intentionally does nothing in the stub
    }
}
