using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: OptionsDescription(this) reads PublicProperties on subject's runtime type for diagnostic rendering only. The runtime stage subclass is preserved via its registration; missing properties degrade the diagnostic readout gracefully.")]
public class ProjectionStage<TOperations, TQuerySession>(int order)
    where TOperations : TQuerySession, IStorageOperations
{
    public int Order { get; } = order;

    private readonly List<IProjectionSource<TOperations, TQuerySession>> _projections = new();

    public OptionsDescription ToDescription(IEventStore store)
    {
        var description = new OptionsDescription(this);
        var @set = description.AddChildSet("Projections");

        foreach (var projection in Projections)
        {
            @set.Rows.Add(projection.Describe(store));
        }

        return description;
    }

    public void Add(IProjectionSource<TOperations, TQuerySession> projection) => _projections.Add(projection);

    public IReadOnlyList<IProjectionSource<TOperations, TQuerySession>> Projections => _projections;

    public ExecutionStage BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory, ShardName parent)
    {
        var executions = Projections.Select(projection =>
        {
            // jasperfx#419: propagate the parent composite's tenant binding to every member stage.
            // When the composite is caught up/rebuilt for a single tenant the parent ShardName carries
            // that tenant id; dropping it here (a bare store-global constructor) was the marten#4679
            // root cause -- every per-tenant member wrote the same store-global progression row.
            var shardName = ShardName.Compose(projection.Name, tenantId: parent.TenantId, version: projection.Version);
            return projection.As<ISubscriptionFactory<TOperations, TQuerySession>>()
                .BuildExecution(store, database, loggerFactory, shardName);
        }).ToArray();

        return new ExecutionStage(executions);
    }

    public ExecutionStage BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, ShardName parent)
    {
        var executions = Projections.Select(projection =>
        {
            // jasperfx#419: see the loggerFactory overload above -- the parent tenant binding must reach
            // every member stage's ShardName so per-tenant progression rows stay distinct.
            var shardName = ShardName.Compose(projection.Name, tenantId: parent.TenantId, version: projection.Version);
            return projection.As<ISubscriptionFactory<TOperations, TQuerySession>>()
                .BuildExecution(store, database, logger, shardName);
        }).ToArray();

        return new ExecutionStage(executions);
    }
}