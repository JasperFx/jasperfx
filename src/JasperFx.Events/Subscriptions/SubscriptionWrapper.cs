using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Subscriptions;

internal class SubscriptionWrapper<TOperations, TQuerySession, TSubscription>: SubscriptionBase<TOperations, TQuerySession, TSubscription> where TOperations : TQuerySession, IStorageOperations
{
    public SubscriptionWrapper(TSubscription subscription) : base(subscription)
    {

    }
}