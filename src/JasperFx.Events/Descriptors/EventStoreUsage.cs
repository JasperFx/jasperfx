using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Descriptors;

public class EventStoreUsage : OptionsDescription
{
    public EventStoreUsage()
    {
    }

    /// <summary>
    /// Build a usage descriptor for <paramref name="subject"/> identified by
    /// <paramref name="subjectUri"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT chain into <see cref="OptionsDescription(object)"/>:
    /// that ctor invokes the reflective property auto-reader, which on a
    /// concrete <c>DocumentStore</c> subject walks <c>Storage</c> /
    /// <c>Advanced</c> / <c>Diagnostics</c> / <c>Options</c> handles and dumps
    /// them into <see cref="OptionsDescription.Children"/> /
    /// <see cref="OptionsDescription.Properties"/>. Those are runtime handles,
    /// not configuration shape; surfacing them in CritterWatch's Configuration
    /// section is noise that operators have to scroll past on every visit.
    ///
    /// Callers (Marten's <c>IEventStore.TryCreateUsage</c>) populate the
    /// first-class fields — <see cref="Database"/>, <see cref="Events"/>,
    /// <see cref="Subscriptions"/>, <see cref="TagTypes"/>,
    /// <see cref="GlobalAggregates"/> — and any extra
    /// <see cref="OptionsDescription.AddValue"/> / <see cref="OptionsDescription.AddChildSet"/>
    /// entries explicitly. Nothing here implicitly walks the subject.
    /// </remarks>
    public EventStoreUsage(Uri subjectUri, object subject)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        Subject = subject.GetType().FullNameInCode();
        SubjectUri = subjectUri;
        DisplayName = DeriveDisplayName(subjectUri);
        Version = subject.GetType().Assembly.GetName().Version?.ToString();
    }

    /// <summary>
    /// Derive a human-facing store label from the clean <see cref="SubjectUri"/>
    /// — the URI's host segment, with the default store's <c>main</c> rendered
    /// as <c>"Main"</c>. Returns <c>"Main"</c> when there is no host.
    /// </summary>
    private static string DeriveDisplayName(Uri subjectUri)
    {
        var host = subjectUri?.Host;
        if (string.IsNullOrEmpty(host))
        {
            return "Main";
        }

        return string.Equals(host, "main", StringComparison.OrdinalIgnoreCase) ? "Main" : host;
    }

    public string Version { get; set; }
    public Uri SubjectUri { get; set; }

    /// <summary>
    /// Human-facing label for this event store, distinct from the type-name
    /// <see cref="OptionsDescription.Subject"/>. For the default store this is
    /// <c>"Main"</c>; for ancillary stores it is the store's clean identifier
    /// (the interface/marker name, e.g. <c>ITarievenStore</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OptionsDescription.Subject"/> follows the OptionsDescription
    /// convention of being the subject's CLR type name
    /// (<c>Marten.DocumentStore</c>, or <c>Marten.DynamicStores.I{T}Implementation</c>
    /// for ancillary stores) — correct as an identity but not something a
    /// monitoring tool should render as a store label. <see cref="DisplayName"/>
    /// gives consumers (e.g. CritterWatch's projection "Store" column) a clean
    /// label without having to scheme-strip <see cref="SubjectUri"/> themselves.
    /// See jasperfx#458.
    /// </para>
    /// <para>
    /// Auto-derived from <see cref="SubjectUri"/> in the constructor, but
    /// settable so an event store can supply richer casing (the URI host is
    /// always lower-cased, so <c>marten://ITarievenStore</c> arrives as
    /// <c>itarievenstore</c>). Mirrors <c>DocumentStoreUsage.StoreName</c>.
    /// </para>
    /// </remarks>
    public string DisplayName { get; set; } = "Main";
    public DatabaseUsage Database { get; set; }
    public List<EventDescriptor> Events { get; set; } = new();
    public List<SubscriptionDescriptor> Subscriptions { get; set; } = new();

    /// <summary>
    /// DCB tag-type registrations on this event store — diagnostic mirror of
    /// <c>EventGraph.TagTypes</c>. First-class typed list so monitoring tools
    /// (e.g. CritterWatch) can render the configuration shape without traversing
    /// the magical <see cref="OptionsDescription.Children"/> / <see cref="OptionsDescription.Sets"/>
    /// dictionaries.
    /// </summary>
    public List<TagTypeDescriptor> TagTypes { get; set; } = new();

    /// <summary>
    /// Aggregates that live outside the multi-tenant boundary in tenanted
    /// configurations — diagnostic mirror of <c>EventGraph.GlobalAggregates</c>,
    /// expressed as a list of CLR type identities.
    /// </summary>
    public List<TypeDescriptor> GlobalAggregates { get; set; } = new();

    /// <summary>
    /// DCB tag-type registrations expressed as the richer
    /// <see cref="DcbTagDescriptor"/> shape used by the event store
    /// explorer. Distinct from <see cref="TagTypes"/> in that the entries
    /// carry a strong <see cref="TypeDescriptor"/> and an
    /// operator-facing description; intended for the Event Modeling and
    /// CritterWatch tag-list views.
    /// </summary>
    public List<DcbTagDescriptor> DcbTagTypes { get; set; } = new();

    /// <summary>
    /// Configured event-type registrations as known to the event store.
    /// Mirror of the store's event registry used by monitoring tools to
    /// list which event aliases are wired up and what their CLR types
    /// are.
    /// </summary>
    public List<EventTypeDescriptor> RegisteredEventTypes { get; set; } = new();

    /// <summary>
    /// Highest event sequence currently persisted in the underlying event-store
    /// table — <c>SELECT max(seq_id) FROM {schema}.mt_events</c> in Marten, the
    /// <c>pc_events</c> equivalent in Polecat. Different from the
    /// HighWaterMark (which is the max-safe-to-read sequence async readers
    /// can trust): this is the absolute physical maximum.
    ///
    /// Null when the implementation hasn't populated it — CritterWatch
    /// renders that as "n/a" rather than zero. The gap between this and
    /// the HighWaterMark is what surfaces CritterWatch#150 signal 2
    /// ("HWM is behind the actual max event sequence").
    /// </summary>
    public long? MaxEventSequence { get; set; }

    /// <summary>
    /// Async-daemon error-handling configuration for normal projection runs —
    /// mirror of <c>StoreOptions.Projections.Errors</c>. Drives whether
    /// monitoring tools surface per-event dead-letter affordances (when
    /// <see cref="ProjectionErrorHandlingDescriptor.SkipApplyErrors"/> is true)
    /// or a "shard halts on error" indicator (when false). Null when the
    /// implementation hasn't populated it; tools should fall back to a
    /// policy-agnostic copy in that case.
    /// </summary>
    /// <remarks>
    /// See JasperFx/ProductSupport#3 for the operator-side motivation:
    /// stop-on-error apps were being shown a "view related dead letters"
    /// button that would never return data.
    /// </remarks>
    public ProjectionErrorHandlingDescriptor? ProjectionErrors { get; set; }

    /// <summary>
    /// Async-daemon error-handling configuration for projection rebuilds —
    /// mirror of <c>StoreOptions.Projections.RebuildErrors</c>. Separate from
    /// <see cref="ProjectionErrors"/> because rebuilds default to stop-on-error
    /// even when normal runs skip — operators rebuilding need to know whether
    /// a poison-pill event will halt the rebuild.
    /// </summary>
    public ProjectionErrorHandlingDescriptor? ProjectionRebuildErrors { get; set; }

    /// <summary>
    /// Effective per-database concurrent rebuild cap — the store's resolved
    /// <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/> (configured via
    /// <c>DaemonSettings.MaxConcurrentRebuildsPerDatabase</c>, jasperfx#420). Null when
    /// the implementation hasn't populated it; monitoring tools should fall back to a
    /// conservative default (typically 1).
    /// </summary>
    /// <remarks>
    /// See JasperFx/CritterWatch#309 for the orchestration consumer (jasperfx#434).
    /// </remarks>
    public int? MaxConcurrentRebuildsPerDatabase { get; set; }

    /// <summary>
    /// Which event/stream metadata this store actually captures — drives
    /// store-aware event/stream query facets in consumers (e.g. CritterWatch)
    /// without sniffing engine-specific config. Null when the implementation
    /// hasn't populated it; tools should treat that as "capabilities unknown".
    /// See jasperfx#475.
    /// </summary>
    public EventMetadataCapabilities? EventMetadata { get; set; }

    /// <summary>
    /// Populates AgentUris on each async SubscriptionDescriptor based on the
    /// agent URI pattern: {agentScheme}://{identity.Type}/{identity.Name}/{databaseId}/{shardName.RelativeUrl}
    /// </summary>
    public void PopulateAgentUris(string agentScheme, EventStoreIdentity identity)
    {
        if (Database == null) return;

        var databaseIds = new List<DatabaseId>();

        foreach (var database in Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            if (!databaseIds.Contains(id))
            {
                databaseIds.Add(id);
            }
        }

        if (Database.MainDatabase != null)
        {
            var mainId = new DatabaseId(Database.MainDatabase.ServerName, Database.MainDatabase.DatabaseName);
            if (!databaseIds.Contains(mainId))
            {
                databaseIds.Add(mainId);
            }
        }

        foreach (var subscription in Subscriptions.Where(x => x.Lifecycle == ProjectionLifecycle.Async))
        {
            var uris = new List<string>();
            foreach (var databaseId in databaseIds)
            {
                foreach (var shardName in subscription.ShardNames)
                {
                    var uri = new Uri($"{agentScheme}://{identity.Type}/{identity.Name}/{databaseId}/{shardName.RelativeUrl}");
                    uris.Add(uri.ToString());
                }
            }

            subscription.AgentUris = uris.ToArray();
        }
    }
}