namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The store-neutral slice of a composite projection's configuration surface that
/// <see cref="CompositeProjectionCompliance{TFixture,TOperations,TQuerySession}" /> needs — adding a
/// snapshot member to a numbered stage.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tiny. A composite's real configuration surface is large and mostly product-typed
/// (<c>Add(IProjection, Action&lt;AsyncOptions&gt;, int)</c> and friends reach each product's own
/// projection and options types), but the one member the compliance suite needs is spelled the same
/// everywhere: <c>Snapshot&lt;T&gt;(int stageNumber)</c>.
/// </para>
/// <para>
/// It declares its own void-returning member rather than naming a shared return type, because the
/// products disagree there and only there — Marten's <c>Snapshot&lt;T&gt;</c> returns a
/// <c>DocumentMappingExpression&lt;T&gt;</c> for further configuration, while Polecat's and Fisher's
/// return void. Nothing in a compliance fact uses that return value, so the seam drops it.
/// </para>
/// </remarks>
public interface IComplianceCompositeBuilder
{
    /// <summary>
    /// Add a self-aggregating snapshot type as a member of the composite, in the given 1-based stage.
    /// </summary>
    void Snapshot<TDoc>(int stageNumber) where TDoc : notnull;

    /// <summary>
    /// Add an already-constructed projection as a member of the composite, in the given 1-based stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typed as the shared <see cref="Projections.ProjectionBase"/> for the same reason
    /// <see cref="IComplianceStoreRegistrar.AddProjection"/> is: this interface is not generic over the
    /// session pair, so the implementing builder casts down to its own product's projection type. Every
    /// projection a suite hands it derives from the product's own base (through the consumer's global
    /// aliases), so the cast is total in practice.
    /// </para>
    /// <para>
    /// Added for jasperfx#917, whose fact needs a stage-2 member that records the synthetic
    /// <c>ProjectionDeleted&lt;TDoc,TId&gt;</c> events stage 1 hands downstream — something a
    /// <see cref="Snapshot{TDoc}"/> member cannot express. The default throws so existing builders keep
    /// compiling; the facts that call it are gated on
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsAddingProjectionsToComposites"/>,
    /// so the default is never reached by a store that has not flipped that gate.
    /// </para>
    /// </remarks>
    void Add(Projections.ProjectionBase projection, int stageNumber)
        => throw new System.NotSupportedException(
            $"{GetType().FullName} does not implement Add(ProjectionBase, int), so it cannot add a custom projection to a composite stage.");
}
