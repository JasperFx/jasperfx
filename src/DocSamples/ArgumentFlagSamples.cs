using JasperFx.CommandLine;

namespace DocSamples;

#region sample_build_input
public class BuildInput
{
    [Description("The target configuration")]
    public string Configuration { get; set; } = "Debug";

    [Description("The output directory")]
    public string OutputPath { get; set; } = "./bin";

    [FlagAlias("verbose", 'v')]
    [Description("Enable verbose output")]
    public bool VerboseFlag { get; set; }

    [FlagAlias("force", 'f')]
    [Description("Force a clean rebuild")]
    public bool ForceFlag { get; set; }

    [Description("Maximum degree of parallelism")]
    public int ParallelCount { get; set; } = 4;
}
#endregion
