using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.OpenTelemetry;

/// <summary>
/// Common OpenTelemetry configuration shared by Critter Stack tools: the connection/session
/// tracking level, the per-tool <see cref="System.Diagnostics.Metrics.Meter"/> used for custom
/// metrics, and the per-tool <see cref="System.Diagnostics.ActivitySource"/> used for spans.
/// </summary>
/// <remarks>
/// <para>
/// Lifted as the common base from Marten's and Polecat's near-identical
/// <c>OpenTelemetryOptions</c>. The only structural difference was the meter name
/// ("Marten" vs "Polecat"), supplied here via the ctor. Part of the Critter Stack 2026 dedupe
/// pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </para>
/// <para>
/// Marten's changeset-metrics surface depended on Marten's own <c>IChangeSet</c>, which is why it
/// stayed behind in Marten, and Fisher then grew an identical copy over its own. Nothing about the
/// mechanism needs to know what a change set is, so <see cref="OpenTelemetryOptions{TChangeSet}"/>
/// lifts it generically and a store keeps its own type and its users' lambdas unchanged.
/// </para>
/// <para>
/// <strong>One name, one <see cref="Meter"/> and one <see cref="ActivitySource"/>, for the
/// process</strong> (jasperfx#1054). The <c>MartenTracing</c>, <c>PolecatTracing</c> and
/// <c>FisherTracing</c> holders were left per-library because the source name is baked in, and
/// Polecat and Fisher then each added a second <see cref="Meter"/> and
/// <see cref="ActivitySource"/> of that same name to answer <c>IEventStore</c>, on top of this
/// class's. A listener subscribed to the name heard all of them. Sharing by name rather than by
/// instance is what makes the fix hold for a second store in the process: each store has its own
/// options, so one per options would rebuild the duplicate a level up, and neither type is released
/// until it is disposed, which nothing here does.
/// </para>
/// </remarks>
public class OpenTelemetryOptions
{
    // Lazy, because GetOrAdd can run its factory twice under a race, and the loser would be exactly the
    // stray same-named instrument these exist to rule out.
    private static readonly ConcurrentDictionary<string, Lazy<Meter>> Meters = new();
    private static readonly ConcurrentDictionary<string, Lazy<ActivitySource>> ActivitySources = new();

    private readonly string _name;
    private ActivitySource? _activitySource;

    /// <param name="meterName">
    /// The name of this tool's <see cref="System.Diagnostics.Metrics.Meter"/> and
    /// <see cref="System.Diagnostics.ActivitySource"/> (e.g. "Marten", "Polecat").
    /// </param>
    public OpenTelemetryOptions(string meterName)
    {
        _name = meterName;
        Meter = Meters.GetOrAdd(meterName, static name => new Lazy<Meter>(() => new Meter(name))).Value;
    }

    /// <summary>
    /// Used to track OpenTelemetry events for opening a connection or exceptions on a
    /// connection (e.g. when a command or data reader is executed). Defaults to
    /// <see cref="TrackLevel.None"/>.
    /// </summary>
    public TrackLevel TrackConnections { get; set; } = TrackLevel.None;

    /// <summary>
    /// The meter used to create custom counters/instruments for this tool.
    /// </summary>
    [IgnoreDescription]
    public Meter Meter { get; }

    /// <summary>
    /// The source this tool's spans start from, named like <see cref="Meter"/> and versioned by the
    /// assembly of the concrete options type, as each tool's tracing holder always was.
    /// </summary>
    /// <remarks>
    /// Created on first use rather than in the constructor. A store that has not moved off its own
    /// holder yet would otherwise gain a second same-named source just by constructing its options,
    /// which is the defect this property removes.
    /// </remarks>
    [IgnoreDescription]
    public ActivitySource ActivitySource => _activitySource ??= ActivitySources.GetOrAdd(_name,
        static (name, version) => new Lazy<ActivitySource>(() => new ActivitySource(name, version)),
        GetType().Assembly.GetName().Version?.ToString()).Value;

    /// <summary>
    /// Start a span from <see cref="ActivitySource"/>, or return null when nothing is listening.
    /// </summary>
    /// <param name="spanName">The span's name.</param>
    /// <param name="parentActivity">
    /// The activity to parent to. Falls back to <see cref="Activity.Current"/>.
    /// </param>
    /// <param name="tags">Tags to start the span with.</param>
    /// <param name="activityKind">The span's kind.</param>
    public Activity? StartActivity(string spanName, Activity? parentActivity = null,
        ActivityTagsCollection? tags = null, ActivityKind activityKind = ActivityKind.Internal)
    {
        // marten#5459: parent to the activity we were handed, NOT to that activity's own parent, or a span
        // under an incoming request hangs off the sending service's span. The ActivityContext rather than
        // the id string, so the sampling flags and tracestate travel with it.
        var parent = parentActivity ?? Activity.Current;

        return ActivitySource.StartActivity(spanName, activityKind, parent?.Context ?? default, tags);
    }
}

/// <summary>
/// <see cref="OpenTelemetryOptions"/> for a store with a commit-level change set, adding the counters
/// an application can have applied to every committed unit of work.
/// </summary>
/// <typeparam name="TChangeSet">
/// The store's own change set type. Generic rather than a shared interface because this is what lets
/// a store's public <c>ExportCounterOnChangeSets</c> keep its signature: the lambda an application has
/// already written takes the store's <c>IChangeSet</c>, and the three stores' <c>Deleted</c> members
/// differ, so no common interface could stand in for it.
/// </typeparam>
/// <remarks>
/// <para>
/// jasperfx#1054. Lifted from Marten's <c>OpenTelemetryOptions</c> and <c>MartenCommitMetrics</c> and
/// Fisher's <c>OpenTelemetryOptions</c> and <c>FisherCommitMetrics</c>, which were the same code.
/// </para>
/// <para>
/// The listener itself stays with the store, because what it derives from is the store's own listener
/// type. It shrinks to one call to <see cref="RecordCommit"/>, and a store registers it only when
/// <see cref="Applications"/> is non-empty, so a store with no counters pays for no listener.
/// </para>
/// </remarks>
public class OpenTelemetryOptions<TChangeSet> : OpenTelemetryOptions
{
    private readonly List<Action<TChangeSet>> _applications = [];

    /// <inheritdoc />
    public OpenTelemetryOptions(string meterName) : base(meterName)
    {
    }

    /// <summary>
    /// The actions applied to every committed change set, one per exported counter.
    /// </summary>
    [IgnoreDescription]
    public IReadOnlyList<Action<TChangeSet>> Applications => _applications;

    /// <summary>
    /// Add a custom counter applied after a unit of work is committed.
    /// </summary>
    /// <param name="name">The counter's name.</param>
    /// <param name="units">The counter's units.</param>
    /// <param name="recordAction">Records against the counter for one committed change set.</param>
    /// <typeparam name="T">The counter's value type.</typeparam>
    public void ExportCounterOnChangeSets<T>(string name, string units, Action<Counter<T>, TChangeSet> recordAction)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(recordAction);

        var counter = Meter.CreateCounter<T>(name, units);

        _applications.Add(commit => recordAction(counter, commit));
    }

    /// <summary>
    /// Apply every exported counter to a committed change set.
    /// </summary>
    /// <remarks>
    /// This runs after the transaction has committed, so a failure surfacing to the caller of
    /// <c>SaveChangesAsync</c> would read as a failed write when the write is already durable. Each
    /// application is therefore caught and logged on its own, and the rest still run.
    /// </remarks>
    public void RecordCommit(TChangeSet commit, ILogger? logger = null)
    {
        for (var i = 0; i < _applications.Count; i++)
        {
            try
            {
                _applications[i](commit);
            }
            catch (Exception e)
            {
                (logger ?? NullLogger.Instance).LogError(e, "Failed to record a commit metric");
            }
        }
    }
}
