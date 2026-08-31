namespace JasperFx.CodeGeneration.Model;

/// <summary>
///     Which variable sources <see cref="IMethodVariables.TryFindVariable" /> is allowed to consult
///     when the method does not already have a variable of the requested type.
/// </summary>
public enum VariableSource
{
    /// <summary>
    ///     Every source, including the IoC container.
    /// </summary>
    All,

    /// <summary>
    ///     Every source except the IoC container.
    /// </summary>
    NotServices,

    /// <summary>
    ///     No sources at all: answer only from what the method already has -- its arguments, its derived
    ///     variables, and the variables its frames already create. A variable source is a factory, so
    ///     <see cref="All" /> and <see cref="NotServices" /> both BUILD what they cannot find; use this
    ///     when the question really is "does this method already have one of these?" and a manufactured
    ///     answer would be wrong. See wolverine#4198.
    /// </summary>
    Existing
}
