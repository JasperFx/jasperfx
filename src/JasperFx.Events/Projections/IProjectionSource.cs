using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Projections;

/// <summary>
/// The types a projection writes, for code that holds a source without its session pair. See
/// <see cref="IProjectionSource{TOperations,TQuerySession}.PublishedTypes" />, which is the member a source
/// implements: every <see cref="IProjectionSource{TOperations,TQuerySession}" /> is one of these with
/// nothing further to do (jasperfx#1102).
/// </summary>
public interface IPublishesTypes
{
    IEnumerable<Type> PublishedTypes();
}

/// <summary>
/// Interface for sources of projections
/// Sources of projections are used to define the behavior how a projection is built for a given projection type
/// Optimized for async usage
/// </summary>
public interface IProjectionSource<TOperations, TQuerySession>: ISubscriptionSource<TOperations, TQuerySession>, IPublishesTypes
    where TOperations : TQuerySession, IStorageOperations
{
    bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)]out IReplayExecutor? executor);

    /// <summary>
    /// Can this projection participate as a member of a <see cref="Composite.CompositeProjection{TOperations,TQuerySession}" />
    /// single-pass rebuild? Custom-grouped (custom-sliced) multi-stream projections do not fan cleanly
    /// into one ordered pass and are excluded initially (jasperfx#407 Phase A). Defaults to true; only
    /// custom-grouped sources opt out by overriding this.
    /// </summary>
    bool CanParticipateInCompositeReplay => true;

    IInlineProjection<TOperations> BuildForInline();

    // Redeclared rather than inherited from IPublishesTypes because a store compiled against this interface
    // calls this member by name, and a source may implement it explicitly (jasperfx#1102).
    new IEnumerable<Type> PublishedTypes();

    IEnumerable<Type> IPublishesTypes.PublishedTypes() => PublishedTypes();
}