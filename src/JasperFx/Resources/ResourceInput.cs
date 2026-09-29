using JasperFx.CommandLine;

namespace JasperFx.Resources;

public class ResourceInput : NetCoreInput
{
    private readonly Lazy<CancellationTokenSource> _cancellation;

    public ResourceInput()
    {
        _cancellation =
            new Lazy<CancellationTokenSource>(() => new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutFlag)));
    }

    [Description("Resource action, default is setup")]
    public ResourceAction Action { get; set; } = ResourceAction.setup;

    // Long form only: -t belongs to --type, the filter people actually reach for (GH-921)
    [Description("Timeout in seconds, default is 60")]
    [FlagAlias("timeout", true)]
    public int TimeoutFlag { get; set; } = 60;

    [IgnoreOnCommandLine] public CancellationTokenSource TokenSource => _cancellation.Value;

    [Description("Optionally filter by resource type")]
    public string? TypeFlag { get; set; }

    [Description("Optionally filter by resource name")]
    public string? NameFlag { get; set; }
}