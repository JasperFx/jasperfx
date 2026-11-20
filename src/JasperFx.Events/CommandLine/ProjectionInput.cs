using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

public enum ProjectionAction
{
    list,
    rebuild,
    run,

    /// <summary>
    /// See, pin, set or clear a projection's persisted start floor (jasperfx#1205)
    /// </summary>
    floor,

    /// <summary>
    /// List parked identities, or ask for a replay or discard of them (marten#6003)
    /// </summary>
    parked
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

    [Description("With the run or rebuild action: first advance the high water mark of every selected database to the latest committed event sequence. Any gap below it is skipped for good")]
    public bool AdvanceFlag { get; set; }

    // jasperfx#1205: the floor action's modes. Long-only, because the parser derives one-letter aliases from
    // the first letter with no collision check, and -s is already --store.

    [Description("With the floor action: show the stored start floor. The default when no other floor option is given")]
    [FlagAlias("show", longAliasOnly: true)]
    public bool ShowFlag { get; set; }

    [Description("With the floor action: set the projection's start floor to the current high water mark")]
    [FlagAlias("pin-current", longAliasOnly: true)]
    public bool PinCurrentFlag { get; set; }

    [Description("With the floor action: set the projection's start floor to this sequence")]
    [FlagAlias("set", longAliasOnly: true)]
    public long? SetFlag { get; set; }

    [Description("With the floor action: clear the projection's start floor. The next rebuild replays from 0, or from the floor configured in code")]
    [FlagAlias("clear", longAliasOnly: true)]
    public bool ClearFlag { get; set; }

    // marten#6003: the parked action's modes. Long-only, for the same reason as the floor action's.

    [Description("With the parked action: ask the node that runs the shard to replay parked identities: one named with --identity, or --all, or --oldest N")]
    [FlagAlias("replay", longAliasOnly: true)]
    public bool ReplayFlag { get; set; }

    [Description("With the parked action: ask the node that runs the shard to discard the parked identity named with --identity, dead-lettering each event that still fails")]
    [FlagAlias("discard", longAliasOnly: true)]
    public bool DiscardFlag { get; set; }

    [Description("With the parked action: the parked identity to replay or discard")]
    [FlagAlias("identity", longAliasOnly: true)]
    public string? IdentityFlag { get; set; }

    [Description("With the parked action and --replay: every parked identity of the projection")]
    [FlagAlias("all", longAliasOnly: true)]
    public bool AllFlag { get; set; }

    [Description("With the parked action and --replay: only the N identities parked first")]
    [FlagAlias("oldest", longAliasOnly: true)]
    public int? OldestFlag { get; set; }

    [Description("With the parked action: wait up to this many seconds for the requested identities to be unparked")]
    [FlagAlias("wait", longAliasOnly: true)]
    public int? WaitFlag { get; set; }

    [Description("With the floor or parked action: don't ask for confirmation before changing anything")]
    [FlagAlias("yes", longAliasOnly: true)]
    public bool YesFlag { get; set; }

    [Description("Maximum number of projections to rebuild concurrently within a single database. Caps the per-database rebuild fan-out for one-off operational rebuilds. Default is unbounded.")]
    [FlagAlias("max-concurrent", longAliasOnly: true)]
    public int? MaxConcurrentFlag { get; set; }

    /// <summary>
    /// jasperfx#420: resolve the effective <see cref="ParallelOptions.MaxDegreeOfParallelism"/> for the
    /// per-database rebuild fan-out. Precedence: the <c>--max-concurrent</c> CLI flag wins for one-off
    /// operational rebuilds; otherwise the store's configured
    /// <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/> default applies; otherwise the historical
    /// unbounded behavior (<c>-1</c>). A non-positive value at any level is treated as unset/unbounded —
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/> of 0 throws, so we must never pass it through.
    /// </summary>
    /// <param name="configuredDefault">
    /// The store-supplied default cap (<see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/>), or null.
    /// </param>
    public int ResolveMaxDegreeOfParallelism(int? configuredDefault = null)
    {
        if (MaxConcurrentFlag is > 0) return MaxConcurrentFlag.Value;
        if (configuredDefault is > 0) return configuredDefault.Value;
        return -1;
    }
}
