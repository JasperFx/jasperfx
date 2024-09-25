namespace JasperFx.Resources;

#region sample_IStatefulResourceSource

/// <summary>
///     Expose multiple stateful resources
/// </summary>
public interface IStatefulResourceSource
{
    IReadOnlyList<IStatefulResource> FindResources();
}

#endregion