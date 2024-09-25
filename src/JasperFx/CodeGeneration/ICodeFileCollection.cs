namespace JasperFx.CodeGeneration;

/// <summary>
///     Code file collection that should be generated with IoC sourced
///     dependencies. This was originally built to support Wolverine message handlers
/// </summary>
public interface ICodeFileCollectionWithServices : ICodeFileCollection
{
}

public interface ICodeFileCollection
{
    /// <summary>
    ///     Appending
    /// </summary>
    string ChildNamespace { get; }

    GenerationRules Rules { get; }
    IReadOnlyList<ICodeFile> BuildFiles();
}