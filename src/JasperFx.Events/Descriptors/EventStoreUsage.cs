using System.Text.Json.Serialization;
using JasperFx.Descriptors;

namespace JasperFx.Events.Descriptors;

public class EventStoreUsage : OptionsDescription
{
    public EventStoreUsage()
    {
    }

    public EventStoreUsage(Uri subjectUri, object subject) : base(subject)
    {
        SubjectUri = subjectUri;
        Version = subject.GetType().Assembly.GetName().Version;
    }
    
    [JsonConverter(typeof(VersionJsonConverter))]
    public Version Version { get; set; }
    public Uri SubjectUri { get; set; }
    public DatabaseUsage Database { get; set; }
    public List<EventDescriptor> Events { get; set; } = new();
    public List<SubscriptionDescriptor> Subscriptions { get; set; } = new();
}