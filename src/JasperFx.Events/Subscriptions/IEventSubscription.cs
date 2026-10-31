using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Subscriptions;

/// <summary>
/// A subscription that runs on any store, written once against the shared document tiers.
/// </summary>
/// <remarks>
/// <para>
/// Marten's, Polecat's and Fisher's <c>ISubscription</c> differ in the type of the session they hand over
/// and the listener they want back, which is the whole of why Wolverine keeps three copies of its
/// subscription runners. The members other than <see cref="ProcessEventsAsync" /> are what those runners
/// read off Wolverine's own <c>IWolverineSubscription</c>, so a store can configure itself from this
/// alone: see <see cref="JasperFxSubscriptionBase{TOperations,TQuerySession,TSubscription}.ConfigureFrom" />.
/// See <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
/// </para>
/// <para>
/// Named <c>IEventSubscription</c> because <c>ISubscription</c> belongs to all three stores, and a file
/// importing a store's <c>Subscriptions</c> namespace and this one would be CS0104.
/// </para>
/// </remarks>
public interface IEventSubscription
{
    /// <summary>
    /// The name this subscription's progress is tracked under.
    /// </summary>
    string SubscriptionName { get; }

    /// <summary>
    /// Greater than 1 is a new subscription that catches up from the beginning, for blue/green deployment.
    /// </summary>
    uint SubscriptionVersion { get; }

    /// <summary>
    /// How the daemon runs this subscription.
    /// </summary>
    AsyncOptions Options { get; }

    /// <summary>
    /// Narrow the events this subscription is sent.
    /// </summary>
    void Filter(IEventFilterable filterable);

    /// <summary>
    /// Handle one page of events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="operations" /> is the write tier and not a committable session, because the daemon
    /// owns the transaction: writes through it, and a participant enlisted on it, commit with the
    /// progression row. Fisher hands its full session here today, and Marten and Polecat their
    /// <c>IDocumentOperations</c>, which is this tier. Wolverine casts those two back to a session to
    /// enlist in them, and <see cref="IDocumentWriteOperations.AddTransactionParticipant" /> is what ends that.
    /// </para>
    /// <para>
    /// Returns <see cref="IDaemonChangeListener" />, which is what Fisher's subscriptions already return
    /// and Marten's and Polecat's <c>IChangeListener</c> adapts onto in a line.
    /// <see cref="IDocumentCommitListener" /> is the wrong type here: that is the session half, a daemon
    /// batch has no session change set to give it, and its own remarks say so.
    /// </para>
    /// </remarks>
    /// <param name="page">The current page of events, in sequence order.</param>
    /// <param name="controller">The shard, for reporting a poison event or requesting a stop.</param>
    /// <param name="operations">Reads and writes that commit with this subscription's progress.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A listener for work that must wait for the commit, or <see cref="NullDaemonChangeListener.Instance" />.
    /// </returns>
    Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentWriteOperations operations, CancellationToken cancellationToken);
}

/// <summary>
/// Registers an <see cref="IEventSubscription" /> on a store. Implemented by every store's projection
/// options through <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}" />.
/// </summary>
/// <remarks>
/// The non-generic route to <c>options.Projections.Subscribe</c>, so that an integration holding a store's
/// options does not have to close three generic arguments to reach it.
/// </remarks>
public interface IEventSubscriptionRegistrar
{
    /// <summary>
    /// Add a subscription to this store. A store that cannot run one refuses here.
    /// </summary>
    /// <param name="subscription"></param>
    /// <param name="configure">Further options, applied after the subscription's own.</param>
    void Subscribe(IEventSubscription subscription, Action<ISubscriptionOptions>? configure = null);
}
