// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// The EventProjection suites declare projection types at file scope, so they cannot reach the
// <TOperations, TQuerySession> pair that the suite classes are generic over. Two more per-consumer
// global aliases close that gap, exactly like ComplianceQuerySession does for the self-aggregating
// fixtures:
//
//     global using ComplianceOperations = Marten.IDocumentOperations;
//     global using ComplianceEventProjection = Marten.Events.Projections.EventProjection;
//
// Aliases (rather than generic base classes) because both products' EventProjection base carries
// store-specific members -- Marten's IProjectionSchemaSource/IMartenRegistrable, Polecat's sealed
// storeEntity override -- so the shared sources want the product's own base type, whatever it is.

global using ComplianceOperations = JasperFx.Events.ComplianceTests.Local.IPlaceholderOperations;
global using ComplianceEventProjection = JasperFx.Events.ComplianceTests.Local.PlaceholderEventProjection;

using JasperFx.Events.Projections;

namespace JasperFx.Events.ComplianceTests.Local;

public interface IPlaceholderOperations: IPlaceholderQuerySession, IStorageOperations
{
    /// <summary>
    /// Called by the registration suite's explicit <c>ApplyAsync</c> override, which is the whole
    /// point of that test -- the source generator has to see the <c>Store&lt;T&gt;</c> call.
    /// </summary>
    void Store<T>(T entity) where T : notnull;
}

public abstract class PlaceholderEventProjection: JasperFxEventProjectionBase<IPlaceholderOperations,
    IPlaceholderQuerySession>
{
    protected override void storeEntity<T>(IPlaceholderOperations ops, T entity) => ops.Store(entity);
}
