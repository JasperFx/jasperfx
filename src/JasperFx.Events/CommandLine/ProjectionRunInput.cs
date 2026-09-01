using JasperFx.CommandLine;
using JasperFx.Core;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Which source slice the <c>projection-run</c> command feeds into the projection.
/// Mirrors CritterWatch's <c>ProjectionRunSourceMode</c> so the CLI and the console's
/// remote stepper agree on the per-mode validation rules (jasperfx#728).
/// </summary>
public enum ProjectionRunSourceMode
{
    /// <summary>Every event of the stream identified by <c>--stream</c>.</summary>
    Stream,

    /// <summary>The <c>--stream</c> events bounded by the inclusive <c>--from</c> / <c>--to</c> stream versions.</summary>
    StreamSlice,

    /// <summary>The (possibly cross-stream) events matching every <c>--tag:name value</c> pair.</summary>
    TagQuery
}

/// <summary>
/// Input for the <c>projection-run</c> command. Every flag is long-form only: the parser
/// derives one-letter aliases from the first letter and has no collision detection, so
/// <c>--stream</c> / <c>--store</c> and <c>--to</c> / <c>--tenant</c> would silently bind
/// whichever handler was built first.
/// </summary>
public class ProjectionRunInput: NetCoreInput
{
    [Description("Name of the registered projection to replay")]
    public string ProjectionName { get; set; } = string.Empty;

    [Description("Stream id whose events drive the replay. Required unless --tag is used")]
    [FlagAlias("stream", longAliasOnly: true)]
    public string? StreamFlag { get; set; }

    [Description("Inclusive lower bound stream version of the slice. Requires --to")]
    [FlagAlias("from", longAliasOnly: true)]
    public long? FromFlag { get; set; }

    [Description("Inclusive upper bound stream version of the slice. Requires --from")]
    [FlagAlias("to", longAliasOnly: true)]
    public long? ToFlag { get; set; }

    [Description("DCB tag to match, written as --tag:<name> <value>. Repeatable. Cannot be combined with --stream")]
    [FlagAlias("tag", longAliasOnly: true)]
    public Dictionary<string, string> TagFlag { get; set; } = new();

    [Description("Tenant partition to read the source events from. Omit for a store-global read")]
    [FlagAlias("tenant", longAliasOnly: true)]
    public string? TenantFlag { get; set; }

    [Description("Subject uri (or bare scheme) of the event store to use. Only needed when the application registers more than one")]
    [FlagAlias("store", longAliasOnly: true)]
    public string? StoreFlag { get; set; }

    [Description("Write the timelines to stdout as JSON instead of a console table")]
    [FlagAlias("json", longAliasOnly: true)]
    public bool JsonFlag { get; set; }

    [Description("Maximum number of source events to feed into the projection. Default is 1000")]
    [FlagAlias("max-events", longAliasOnly: true)]
    public int MaxEventsFlag { get; set; } = 1000;

    /// <summary>
    /// Source mode implied by the flags. Tags win, then a version bound, then the bare stream read —
    /// so the mode is never spelled out twice and can never disagree with the flags that drive it.
    /// </summary>
    public ProjectionRunSourceMode SourceMode
    {
        get
        {
            if (TagFlag.Count > 0) return ProjectionRunSourceMode.TagQuery;
            if (FromFlag.HasValue || ToFlag.HasValue) return ProjectionRunSourceMode.StreamSlice;
            return ProjectionRunSourceMode.Stream;
        }
    }

    /// <summary>
    /// Per-mode required-field rules, mirroring CritterWatch's <c>RequestProjectionRunHandler</c>.
    /// Returns the operator-facing message for the first violation, or null when the input is usable.
    /// </summary>
    public string? Validate()
    {
        if (ProjectionName.IsEmpty())
        {
            return "A projection name is required";
        }

        if (MaxEventsFlag <= 0)
        {
            return "--max-events must be greater than zero";
        }

        switch (SourceMode)
        {
            case ProjectionRunSourceMode.TagQuery:
                // CritterWatch's handler ignores the stream id in tag mode because its UI never sends
                // both. A CLI operator who types both is expressing an intent the command cannot honor,
                // so say so rather than silently dropping half of it.
                if (StreamFlag.IsNotEmpty())
                {
                    return "--stream cannot be combined with --tag; a tag query is not stream-anchored";
                }

                if (FromFlag.HasValue || ToFlag.HasValue)
                {
                    return "--from / --to cannot be combined with --tag; version bounds only apply to a stream slice";
                }

                return null;

            case ProjectionRunSourceMode.StreamSlice:
                if (StreamFlag.IsEmpty())
                {
                    return "--stream is required";
                }

                if (!FromFlag.HasValue || !ToFlag.HasValue)
                {
                    return "--from and --to are both required for a stream slice";
                }

                if (FromFlag > ToFlag)
                {
                    return "--from must be less than or equal to --to";
                }

                return null;

            default:
                return StreamFlag.IsEmpty() ? "--stream is required" : null;
        }
    }

    /// <summary>
    /// Stable, human-readable identity of the source slice, using the same per-mode encoding as
    /// CritterWatch's <c>RequestProjectionRunHandler.BuildSourceKey</c> so a CLI run and a console
    /// run over the same slice are recognisably the same thing.
    /// </summary>
    public string SourceKey =>
        SourceMode switch
        {
            ProjectionRunSourceMode.Stream => StreamFlag ?? string.Empty,
            ProjectionRunSourceMode.StreamSlice => $"{StreamFlag ?? string.Empty}@{FromFlag}..{ToFlag}",
            ProjectionRunSourceMode.TagQuery => buildTagSourceKey(),
            _ => StreamFlag ?? string.Empty
        };

    private string buildTagSourceKey()
    {
        if (TagFlag.Count == 0) return "tags::";

        var pairs = TagFlag
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}");

        return "tags::" + string.Join("&", pairs);
    }
}
