using System.Text.Json.Serialization;
using JasperFx.Core.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Descriptors;

public class SubscriptionDescriptor : OptionsDescription
{
    public SubscriptionType SubscriptionType { get; }

    [JsonConstructor]
    public SubscriptionDescriptor(SubscriptionType subscriptionType)
    {
        SubscriptionType = subscriptionType;
    }

    // TODO -- unit test this
    public SubscriptionDescriptor(ISubscriptionSource subject) : base(subject)
    {
        SubscriptionType = subject.Type;
        Name = subject.Name;
        Version = subject.Version;
        ShardNames = subject.ShardNames();
        Lifecycle = subject.Lifecycle;
    }

    public ProjectionLifecycle Lifecycle { get; set; }
    
    public ShardName[] ShardNames { get; set; }

    public string Name { get; set; }
    public uint Version { get; set; }

    public List<EventDescriptor> Events { get; set; } = new();
}