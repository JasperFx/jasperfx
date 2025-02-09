using JasperFx.Core.Reflection;

namespace JasperFx.Core.Descriptions;

public class EventStoreUsage : OptionsDescription
{
    public EventStoreUsage()
    {
    }

    public EventStoreUsage(Type storeType, object options) : base(options)
    {
        StoreIdentifier = storeType.FullNameInCode();
    }

    public string StoreIdentifier { get; set; }
    public DatabaseUsage Database { get; set; }
    public List<EventDescriptor> Events { get; set; } = new();
    public List<SubscriptionDescriptor> Subscriptions { get; set; } = new();
}