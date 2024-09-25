namespace JasperFx.CommandLine.Descriptions;

/// <summary>
///     Optional interface for exposing specialized console output
///     in the "describe" command
/// </summary>
public interface IWriteToConsole
{
    Task WriteToConsole();
}