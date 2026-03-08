using JasperFx.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Descriptors;

public class EventStoreUsage : OptionsDescription
{
    public EventStoreUsage()
    {
    }

    public EventStoreUsage(Uri subjectUri, object subject) : base(subject)
    {
        SubjectUri = subjectUri;
        Version = subject.GetType().Assembly.GetName().Version?.ToString();
    }

    public string Version { get; set; }
    public Uri SubjectUri { get; set; }
    public DatabaseUsage Database { get; set; }
    public List<EventDescriptor> Events { get; set; } = new();
    public List<SubscriptionDescriptor> Subscriptions { get; set; } = new();

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