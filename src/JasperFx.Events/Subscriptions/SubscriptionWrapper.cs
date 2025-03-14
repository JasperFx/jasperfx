using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Subscriptions;

internal class SubscriptionWrapper<TOperations, TQuerySession, TSubscription> : JasperFxSubscriptionBase<TOperations, TQuerySession
    , TSubscription> where TOperations : TQuerySession, IStorageOperations
{
    public SubscriptionWrapper(TSubscription subscription) : base(subscription)
    {
    }

    public override SubscriptionDescriptor Describe()
    {
        var descriptor = new SubscriptionDescriptor(this, SubscriptionType.Subscription);
        descriptor.AddValue("Subscription", typeof(TSubscription).FullNameInCode());
        return descriptor;
    }
}