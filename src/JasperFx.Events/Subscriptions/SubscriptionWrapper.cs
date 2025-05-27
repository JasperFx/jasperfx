using JasperFx.Core.Reflection;
using JasperFx.Events.Descriptors;

namespace JasperFx.Events.Subscriptions;

internal class SubscriptionWrapper<TOperations, TQuerySession, TSubscription> : JasperFxSubscriptionBase<TOperations, TQuerySession
    , TSubscription> where TOperations : TQuerySession, IStorageOperations
{
    public SubscriptionWrapper(TSubscription subscription) : base(subscription)
    {
    }

    public override SubscriptionDescriptor Describe(IEventStore store)
    {
        var descriptor = new SubscriptionDescriptor(this, store);
        descriptor.AddValue("Subscription", typeof(TSubscription).FullNameInCode());
        return descriptor;
    }
}