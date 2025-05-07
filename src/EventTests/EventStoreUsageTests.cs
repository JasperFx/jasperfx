using JasperFx.Core.Descriptors;
using JasperFx.Events.Descriptors;

namespace EventTests;

public class EventStoreUsageTests
{
    [Fact]
    public void can_serialize()
    {
        var usage = new EventStoreUsage(new Uri("marten://store"), this);
        usage.Database = new DatabaseUsage
        {
            Cardinality = DatabaseCardinality.Single,
            Databases = [new DatabaseDescriptor()]
        };
        
        usage.Events.Add(new EventDescriptor("This", TypeDescriptor.For(GetType())));
        usage.Subscriptions.Add(new SubscriptionDescriptor(SubscriptionType.Subscription));
        
        usage.ShouldBeSerializable();
    }
}