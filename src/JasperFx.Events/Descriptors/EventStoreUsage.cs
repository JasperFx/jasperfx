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
        Version = subject.GetType().Assembly.GetName().Version?.ToString();
    }

    public string Version { get; set; }
    public Uri SubjectUri { get; set; }
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