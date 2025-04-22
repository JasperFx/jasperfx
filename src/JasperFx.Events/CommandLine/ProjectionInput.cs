using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

public enum ProjectionAction
{
    list,
    rebuild,
    run
}

public class ProjectionInput: NetCoreInput
{
    [Description("Action to execute. Default is to run continuously")]
    public ProjectionAction Action { get; set; } = ProjectionAction.run;
    
    [Description("If specified, only run or rebuild the named projection")]
    public string? ProjectionFlag { get; set; }

    [Description("If specified, only execute against the named event store by a subject uri match. Does not apply with only one store")]
    public string? StoreFlag { get; set; }

    [Description("If specified, only execute against the named event database within the specified store(s). Does not apply with only one database")]
    public string? DatabaseFlag { get; set; }

    [Description("If specified, only executes against the whole database containing this tenant")]
    public string? TenantFlag { get; set; }

    [Description("If specified, use this shard timeout value for daemon")]
    [FlagAlias("shard-timeout", longAliasOnly: true)]
    public string? ShardTimeoutFlag { get; set; }

    [Description("If specified, advances the projection high water mark to the latest event sequence")]
    public bool AdvanceFlag { get; set; }
}
