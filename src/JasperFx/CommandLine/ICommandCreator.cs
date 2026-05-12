using System.Diagnostics.CodeAnalysis;

namespace JasperFx.CommandLine;

/// <summary>
///     Service locator for command types. The default just uses Activator.CreateInstance().
///     Can be used to plug in IoC construction in JasperFx applications.
/// </summary>
/// <remarks>
///     Implementations resolve concrete <see cref="IJasperFxCommand"/> and input model
///     types reflectively. The annotations propagate the requirement that callers
///     either supply types whose constructors / properties survive trimming, or
///     opt in via the published <c>[RequiresUnreferencedCode]</c> annotation.
///     AOT/trim-clean apps consume commands through the source-generated manifest
///     (see <see cref="CommandFactory.TryRegisterFromGeneratedManifest"/>) rather
///     than reflective scanning, so this interface's annotations are the precise
///     punch-list AOT consumers see when they call into the reflective path.
/// </remarks>
public interface ICommandCreator
{
    [RequiresUnreferencedCode("CreateCommand instantiates commandType via reflection (Activator.CreateInstance or DI). Public constructors and InjectService properties must survive trimming.")]
    IJasperFxCommand CreateCommand(Type commandType);

    [RequiresUnreferencedCode("CreateModel instantiates modelType via reflection (Activator.CreateInstance). Public parameterless constructor must survive trimming.")]
    object CreateModel(Type modelType);
}