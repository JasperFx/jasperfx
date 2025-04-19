namespace JasperFx.Events.Aggregation;

/// <summary>
/// Small strategy to set the identity of an entity
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TId"></typeparam>
public interface IIdentitySetter<in T, in TId>
{
    /// <summary>
    ///     Assign the given identity to the document
    /// </summary>
    /// <param name="document"></param>
    /// <param name="identity"></param>
    void SetIdentity(T document, TId identity);
}
