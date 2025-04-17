using System.Text.Json.Serialization;

namespace JasperFx.Core.Descriptors;

public class SubscriptionDescriptor : OptionsDescription
{
    public SubscriptionType SubscriptionType { get; }

    [JsonConstructor]
    public SubscriptionDescriptor(SubscriptionType subscriptionType)
    {
        SubscriptionType = subscriptionType;
    }

    public SubscriptionDescriptor(object subject, SubscriptionType subscriptionType) : base(subject)
    {
        SubscriptionType = subscriptionType;
    }

    public List<EventDescriptor> Events { get; set; } = new();
}