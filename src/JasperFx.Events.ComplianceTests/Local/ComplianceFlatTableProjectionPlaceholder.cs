// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// FlatTableProjectionCompliance is the one suite whose shared type cannot be reached by an alias.
// Every product's flat-table projection base takes constructor arguments describing where the table
// lives and those signatures genuinely differ (Marten a SchemaNameSource enum, Polecat a literal
// schema name), so no single base(...) call satisfies both. Each consumer therefore supplies a
// partial carrying the constructor and the primary key column:
//
//     public partial class ComplianceFlatTableProjection : FlatTableProjection
//     {
//         public ComplianceFlatTableProjection() : base(TableName, SchemaNameSource.DocumentSchema)
//         {
//             Table.AddColumn<Guid>("id").AsPrimaryKey();
//             ConfigureMappings();
//         }
//     }
//
// This is that partial, for the JasperFx repo. It is also the closest thing the library has to a
// written-down statement of the surface a flat-table base must expose, so a new consumer can read
// it as the contract to implement -- everything below is called by ConfigureMappings.
//
// Return types are deliberately void here. Both products return their own dialect's
// Table.ColumnExpression (two unrelated nested types with no shared base), and the shared suite
// never names or chains off the result, so the placeholder does not invent a common one.

using System;
using System.Linq.Expressions;
using JasperFx.Events.Projections;

namespace JasperFx.Events.ComplianceTests.Local;

/// <summary>
/// Stand-in for each product's flat-table projection base. Members exist only where
/// <c>ComplianceFlatTableProjection.ConfigureMappings</c> actually calls them.
/// </summary>
public abstract class PlaceholderFlatTableProjection: ProjectionBase
{
    public void Project<TEvent>(Action<PlaceholderStatementMap<TEvent>> configure,
        Expression<Func<TEvent, object>>? primaryKeySource = null)
    {
    }

    public void Delete<TEvent>(Expression<Func<TEvent, object>>? primaryKeySource = null)
    {
    }
}

/// <summary>
/// Stand-in for each product's <c>StatementMap&lt;TEvent&gt;</c>. The two are signature-identical
/// today; only the declaring namespace and the <c>Table.ColumnExpression</c> return type differ.
/// </summary>
public class PlaceholderStatementMap<TEvent>
{
    public void Map<TValue>(Expression<Func<TEvent, TValue>> members, string? columnName = null)
    {
    }

    public void Increment<TValue>(Expression<Func<TEvent, TValue>> members, string? columnName = null)
    {
    }

    public void Increment(string columnName)
    {
    }

    public void Decrement<TValue>(Expression<Func<TEvent, TValue>> members, string? columnName = null)
    {
    }

    public void Decrement(string columnName)
    {
    }

    public void SetValue(string columnName, string value)
    {
    }

    public void SetValue(string columnName, int value)
    {
    }
}
