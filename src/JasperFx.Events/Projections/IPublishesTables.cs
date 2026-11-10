namespace JasperFx.Events.Projections;

/// <summary>
///     A projection whose output lives in tables no document type maps to, such as a flat table.
/// </summary>
/// <remarks>
///     A document table is found from the projection's document types, so a table with no document type is
///     invisible to that lookup, and a rebuild would replay onto the rows the previous run left.
///     <see cref="ProjectionTeardownPlan" /> reads these names under the same rules as
///     <see cref="AsyncOptions.DeleteDataInTableOnTeardown" />, including
///     <see cref="AsyncOptions.TeardownDataOnRebuild" />. Lifted from Fisher, see jasperfx#1046.
/// </remarks>
public interface IPublishesTables
{
    /// <summary>
    ///     The tables this projection writes, spelled the way the store's teardown expects them.
    /// </summary>
    IEnumerable<string> PublishedTableNames();
}
