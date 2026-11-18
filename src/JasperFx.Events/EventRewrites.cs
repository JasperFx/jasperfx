using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events;

/// <summary>
///     jasperfx#1215: which stored events a rewrite reads and writes back, as plain data that CritterWatch or
///     application code can send. A Wolverine job runs it, one database at a time, through
///     <see cref="IEventStore.RewriteStoredEventsAsync" />.
/// </summary>
/// <remarks>
///     Every selected event is read the way the store reads it today and written back the way an append would store
///     it today, after the named transforms. Rows that would come out the same are left alone.
/// </remarks>
public sealed class EventRewriteDefinition
{
    /// <summary>
    ///     The name of the rewrite. Its ceiling and cursor are stored under <see cref="EventRangeMarks.RewriteKey" />.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The event type names (the aliases stored on each event) to rewrite. A transform's event type is included
    ///     without being listed here.
    /// </summary>
    public List<string> EventTypes { get; set; } = new();

    /// <summary>
    ///     Rewrite every event in the store. Events of a type this application no longer knows are left as they are
    ///     and counted in <see cref="EventRewriteProgress.UnknownTypes" />.
    /// </summary>
    public bool AllEventTypes { get; set; }

    /// <summary>
    ///     The names of transforms registered with <see cref="EventRegistry.AddRewriteTransform{T}" />, applied in
    ///     this order. An unknown name refuses the whole request.
    /// </summary>
    public List<string> Transforms { get; set; } = new();

    /// <summary>
    ///     Only read the selected events still stored as JSON, and leave the ones already stored in binary alone.
    /// </summary>
    public bool OnlyEventsStoredAsJson { get; set; }

    /// <summary>
    ///     Events read per page, and written back with the page's cursor in one transaction. Default 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     Wait between pages, to leave the database room for the application. Default none.
    /// </summary>
    public TimeSpan PauseBetweenBatches { get; set; } = TimeSpan.Zero;

    /// <summary>
    ///     Read and compare everything, but write nothing, not even the cursor.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    ///     The tenants to rewrite, one after another, each with its own cursor and ceiling. Empty means the whole
    ///     database, or every tenant under tenant-partitioned events.
    /// </summary>
    public List<string> Tenants { get; set; } = new();
}

/// <summary>
///     jasperfx#1215: one page of a stored-event rewrite, or the totals of one tenant.
/// </summary>
/// <remarks>
///     <see cref="EventRewriteRun.OnPage" /> receives the counts of the page alone. The list
///     <see cref="IEventStore.RewriteStoredEventsAsync" /> returns holds the totals of each tenant for that call.
/// </remarks>
/// <param name="TenantId">The tenant, or null for the whole database.</param>
/// <param name="Sequence">The cursor after the page: the last sequence finished, or the ceiling once complete.</param>
/// <param name="Visited">Events read.</param>
/// <param name="Rewritten">Events written back, or that would be in a dry run.</param>
/// <param name="Skipped">Events another transaction changed between the read and the write.</param>
/// <param name="BytesBefore">Stored payload size of the rewritten events before.</param>
/// <param name="BytesAfter">Payload size of the rewritten events after.</param>
/// <param name="UnknownTypes">Events of a type this application no longer knows, left as they are.</param>
/// <param name="Completed">Whether every selected event up to the ceiling has been visited.</param>
public sealed record EventRewriteProgress(
    string? TenantId,
    long Sequence,
    long Visited,
    long Rewritten,
    long Skipped,
    long BytesBefore,
    long BytesAfter,
    long UnknownTypes,
    bool Completed);

/// <summary>
///     jasperfx#1215: one call to <see cref="IEventStore.RewriteStoredEventsAsync" /> against one database, with a
///     cursor and a ceiling for each tenant it covers.
/// </summary>
/// <remarks>
/// <para>
///     The store writes each page's cursor under <see cref="CursorKey" /> in the same transaction as the page, so a
///     crash never applies a transform to the same event twice, and it stops after each tenant's
///     <see cref="EventRangeCursor.StopAfter" />, so events appended after the request are left alone.
/// </para>
/// <para>
///     Use <see cref="FromMarksAsync" /> to build one from the stored marks, capturing the ceilings on the first
///     request.
/// </para>
/// </remarks>
public sealed class EventRewriteRun
{
    public EventRewriteRun(IEventDatabase database, EventRewriteDefinition definition,
        IReadOnlyList<EventRangeCursor> cursors)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentException.ThrowIfNullOrEmpty(definition.Name, nameof(definition));
        Cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    }

    /// <summary>The database to rewrite.</summary>
    public IEventDatabase Database { get; }

    /// <summary>What to rewrite.</summary>
    public EventRewriteDefinition Definition { get; }

    /// <summary>One cursor per tenant, run in this order. A single null-tenant cursor covers the whole database.</summary>
    public IReadOnlyList<EventRangeCursor> Cursors { get; }

    /// <summary>
    ///     Called after each page is committed, with that page's counts and the cursor stored with it.
    /// </summary>
    public Func<EventRewriteProgress, CancellationToken, Task>? OnPage { get; set; }

    /// <summary>The key the ceilings are stored under.</summary>
    public string CeilingKey => EventRangeMarks.RewriteKey(Definition.Name);

    /// <summary>The key the store writes each page's cursor under, in the page's own transaction.</summary>
    public string CursorKey => EventRangeMarks.CursorKey(CeilingKey);

    /// <summary>
    ///     Build a run from the stored marks: for each tenant (the whole database when
    ///     <see cref="EventRewriteDefinition.Tenants" /> is empty), the stored ceiling or the current head sequence
    ///     inserted if absent, and the stored cursor or 0.
    /// </summary>
    public static async Task<EventRewriteRun> FromMarksAsync(IEventDatabase database, EventRewriteDefinition definition,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(definition);

        var ceilingKey = EventRangeMarks.RewriteKey(definition.Name);
        IReadOnlyList<string?> tenants = definition.Tenants.Count == 0
            ? [null]
            : definition.Tenants.Select(x => (string?)x).ToArray();

        var cursors = new List<EventRangeCursor>();
        foreach (var tenantId in tenants)
        {
            cursors.Add(await EventRangeCursor.FromMarksAsync(database, ceilingKey, tenantId,
                t => headSequenceAsync(database, tenantId, t), token).ConfigureAwait(false));
        }

        return new EventRewriteRun(database, definition, cursors);
    }

    // The tenant's own head under tenant-partitioned events. Sequences are store-wide otherwise, and a store that
    // does not partition by tenant answers the per-tenant overload with NotSupportedException.
    private static async Task<long> headSequenceAsync(IEventDatabase database, string? tenantId,
        CancellationToken token)
    {
        if (tenantId == null) return await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);

        try
        {
            return await database.FetchHighestEventSequenceNumber(tenantId, token).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     jasperfx#1215: a named transform registered with <see cref="EventRegistry.AddRewriteTransform{T}" />, which an
///     <see cref="EventRewriteDefinition" /> refers to by name.
/// </summary>
public sealed class EventRewriteTransform
{
    private readonly Func<object, object> _transform;

    internal EventRewriteTransform(string name, Type eventType, Func<object, object> transform)
    {
        Name = name;
        EventType = eventType;
        _transform = transform;
    }

    /// <summary>The name a definition refers to it by.</summary>
    public string Name { get; }

    /// <summary>The event type it applies to, including types that can be cast to it.</summary>
    public Type EventType { get; }

    /// <summary>Whether this transform applies to <paramref name="eventData" />.</summary>
    public bool AppliesTo(object eventData) => EventType.IsInstanceOfType(eventData);

    /// <summary>
    ///     The transformed event data. Throws when the transform does not apply to <paramref name="eventData" />.
    /// </summary>
    public object Apply(object eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (!AppliesTo(eventData))
        {
            throw new ArgumentOutOfRangeException(nameof(eventData),
                $"Rewrite transform '{Name}' applies to {EventType.FullName}, not {eventData.GetType().FullName}.");
        }

        return _transform(eventData)
               ?? throw new InvalidOperationException($"Rewrite transform '{Name}' returned null.");
    }
}

/// <summary>
///     jasperfx#1215: an <see cref="EventRewriteDefinition" /> named transforms that are not registered, so the whole
///     request is refused before anything is read.
/// </summary>
public sealed class UnknownRewriteTransformException : Exception
{
    public UnknownRewriteTransformException(string rewriteName, IReadOnlyList<string> unknownNames)
        : base($"Event rewrite '{rewriteName}' names rewrite transforms that are not registered: " +
               $"{string.Join(", ", unknownNames)}. Register them with EventRegistry.AddRewriteTransform<T>(name, transform).")
    {
        RewriteName = rewriteName;
        UnknownNames = unknownNames;
    }

    public string RewriteName { get; }
    public IReadOnlyList<string> UnknownNames { get; }
}

public partial class EventRegistry
{
    private readonly Dictionary<string, EventRewriteTransform> _rewriteTransforms = new(StringComparer.Ordinal);

    /// <summary>
    ///     jasperfx#1215: register a transform that an <see cref="EventRewriteDefinition" /> can name, to change events
    ///     read as <typeparamref name="T" /> (or types that can be cast to it) before a rewrite writes them back.
    /// </summary>
    /// <remarks>
    ///     This rewrites history. Nothing derived from the old events, such as projections, natural keys or tags, is
    ///     updated. A transform need not be idempotent: the store writes its cursor with each page, so no event goes
    ///     through it twice in one run.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A transform with this name is already registered.</exception>
    public void AddRewriteTransform<T>(string name, Func<T, T> transform) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(transform);

        if (_rewriteTransforms.ContainsKey(name))
        {
            throw new InvalidOperationException($"A rewrite transform named '{name}' is already registered.");
        }

        _rewriteTransforms[name] = new EventRewriteTransform(name, typeof(T), x => transform((T)x));
    }

    /// <summary>
    ///     jasperfx#1215: every registered rewrite transform.
    /// </summary>
    public IReadOnlyList<EventRewriteTransform> RewriteTransforms() => _rewriteTransforms.Values.ToArray();

    /// <summary>
    ///     jasperfx#1215: the transform registered under <paramref name="name" />, if any.
    /// </summary>
    public bool TryFindRewriteTransform(string name, [NotNullWhen(true)] out EventRewriteTransform? transform)
        => _rewriteTransforms.TryGetValue(name, out transform);

    /// <summary>
    ///     jasperfx#1215: the transforms <paramref name="definition" /> names, in its order. A store calls this before it
    ///     reads anything.
    /// </summary>
    /// <exception cref="UnknownRewriteTransformException">Any name is not registered.</exception>
    public IReadOnlyList<EventRewriteTransform> FindRewriteTransforms(EventRewriteDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var unknown = definition.Transforms.Where(x => !_rewriteTransforms.ContainsKey(x)).Distinct().ToArray();
        if (unknown.Length > 0)
        {
            throw new UnknownRewriteTransformException(definition.Name, unknown);
        }

        return definition.Transforms.Select(x => _rewriteTransforms[x]).ToArray();
    }
}
