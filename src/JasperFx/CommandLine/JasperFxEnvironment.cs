namespace JasperFx.CommandLine;

public static class JasperFxEnvironment
{
    /// <summary>
    ///     If using JasperFx as the run command in .Net Core applications with WebApplication,
    ///     this will force JasperFx to automatically start up the IHost when the Program.Main()
    ///     method runs. Very useful for WebApplicationFactory testing
    /// </summary>
    public static bool AutoStartHost { get; set; }
    
    public static bool RunQuiet { get; set; }
}