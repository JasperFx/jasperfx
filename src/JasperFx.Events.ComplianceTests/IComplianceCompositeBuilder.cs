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
}
