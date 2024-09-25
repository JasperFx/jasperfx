using Microsoft.Extensions.Hosting;

namespace JasperFx.CommandLine;

/// <summary>
///     Interface used to get access to the HostBuilder from command inputs.
/// </summary>
public interface IHostBuilderInput
{
    [IgnoreOnCommandLine] IHostBuilder HostBuilder { get; set; }
}