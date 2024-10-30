namespace JasperFx.CommandLine;

/// <summary>
///     Service locator for command types. The default just uses Activator.CreateInstance().
///     Can be used to plug in IoC construction in JasperFx applications
/// </summary>
public interface ICommandCreator
{
    IJasperFxCommand CreateCommand(Type commandType);
    object CreateModel(Type modelType);
}