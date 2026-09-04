using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Input for the <c>stream-query</c> command. Every flag is long-form only for the same reason as
/// <see cref="ProjectionRunInput"/> and <see cref="EventQueryInput"/>: the parser derives
/// one-letter aliases from the first letter with no collision detection, and this surface has
/// <c>--store</c>/<c>--stream</c>-adjacent spellings plus four timestamp flags competing for the
/// same letters.
/// </summary>
public class StreamQueryInput: NetCoreInput
{
    [Description("Aggregate type the streams were started as — a CLR type name (simple or full), resolved against the application's loaded types")]
    [FlagAlias("aggregate-type", longAliasOnly: true)]
    public string? AggregateTypeFlag { get; set; }

    [Description("Only streams whose version (event count) is at least this")]
    [FlagAlias("min-version", longAliasOnly: true)]
    public long? MinVersionFlag { get; set; }

    [Description("Only streams whose un-compacted growth exceeds this: Version - CompactedVersion > N, the compaction-policy predicate")]
    [FlagAlias("version-above-compacted", longAliasOnly: true)]
    public long? VersionAboveCompactedFlag { get; set; }

    [Description("Filter on the archived flag: 'true' for archived streams only, 'false' for live only. Omit for both")]
    [FlagAlias("archived", longAliasOnly: true)]
    public bool? ArchivedFlag { get; set; }

    [Description("Inclusive lower bound on the stream's creation time, any DateTimeOffset format (e.g. 2026-09-01T00:00:00Z)")]
    [FlagAlias("created-from", longAliasOnly: true)]
    public string? CreatedFromFlag { get; set; }

    [Description("Inclusive upper bound on the stream's creation time")]
    [FlagAlias("created-to", longAliasOnly: true)]
    public string? CreatedToFlag { get; set; }

    [Description("Inclusive lower bound on the stream's last-append time")]
    [FlagAlias("updated-from", longAliasOnly: true)]
    public string? UpdatedFromFlag { get; set; }

    [Description("Inclusive upper bound on the stream's last-append time")]
    [FlagAlias("updated-to", longAliasOnly: true)]
    public string? UpdatedToFlag { get; set; }

    [Description("Tenant partition to scope the query to. Omit for a store-global read")]
    [FlagAlias("tenant", longAliasOnly: true)]
    public string? TenantFlag { get; set; }

    [Description("1-based page number. Default is 1")]
    [FlagAlias("page", longAliasOnly: true)]
    public int PageFlag { get; set; } = 1;

    [Description("Page size. Default is 50")]
    [FlagAlias("page-size", longAliasOnly: true)]
    public int PageSizeFlag { get; set; } = 50;

    [Description("Subject uri (or bare scheme) of the event store to query. Only needed when the application registers more than one")]
    [FlagAlias("store", longAliasOnly: true)]
    public string? StoreFlag { get; set; }

    [Description("Output rendering: json (default, for agents and scripts) or text (a console table)")]
    [FlagAlias("format", longAliasOnly: true)]
    public EventQueryFormat FormatFlag { get; set; } = EventQueryFormat.Json;

    /// <summary>
    /// Operator-facing message for the first invalid flag, or null when the input is usable.
    /// </summary>
    public string? Validate()
    {
        if (PageFlag < 1)
        {
            return "--page must be 1 or greater";
        }

        if (PageSizeFlag < 1)
        {
            return "--page-size must be greater than zero";
        }

        if (MinVersionFlag is < 0)
        {
            return "--min-version cannot be negative";
        }

        if (VersionAboveCompactedFlag is < 0)
        {
            return "--version-above-compacted cannot be negative";
        }

        if (CreatedFromFlag.IsNotEmpty() && !DateTimeOffset.TryParse(CreatedFromFlag, out _))
        {
            return $"--created-from '{CreatedFromFlag}' is not a recognizable timestamp";
        }

        if (CreatedToFlag.IsNotEmpty() && !DateTimeOffset.TryParse(CreatedToFlag, out _))
        {
            return $"--created-to '{CreatedToFlag}' is not a recognizable timestamp";
        }

        if (UpdatedFromFlag.IsNotEmpty() && !DateTimeOffset.TryParse(UpdatedFromFlag, out _))
        {
            return $"--updated-from '{UpdatedFromFlag}' is not a recognizable timestamp";
        }

        if (UpdatedToFlag.IsNotEmpty() && !DateTimeOffset.TryParse(UpdatedToFlag, out _))
        {
            return $"--updated-to '{UpdatedToFlag}' is not a recognizable timestamp";
        }

        if (parseTimestamp(CreatedFromFlag) is { } createdFrom && parseTimestamp(CreatedToFlag) is { } createdTo &&
            createdFrom > createdTo)
        {
            return "--created-from must be less than or equal to --created-to";
        }

        if (parseTimestamp(UpdatedFromFlag) is { } updatedFrom && parseTimestamp(UpdatedToFlag) is { } updatedTo &&
            updatedFrom > updatedTo)
        {
            return "--updated-from must be less than or equal to --updated-to";
        }

        return null;
    }

    public DateTimeOffset? CreatedFrom => parseTimestamp(CreatedFromFlag);
    public DateTimeOffset? CreatedTo => parseTimestamp(CreatedToFlag);
    public DateTimeOffset? UpdatedFrom => parseTimestamp(UpdatedFromFlag);
    public DateTimeOffset? UpdatedTo => parseTimestamp(UpdatedToFlag);

    /// <summary>
    /// Compose the flags onto the stream-state queryable as <c>Where</c> clauses. Pure queryable
    /// composition — no execution — so the mapping is unit-testable against an in-memory
    /// <see cref="IQueryable{T}"/> with no store.
    /// </summary>
    /// <param name="queryable">The source, from <see cref="IReadOnlyEventStore.QueryStreamStates"/>.</param>
    /// <param name="aggregateType">
    /// The resolved CLR type for <see cref="AggregateTypeFlag"/>, or null when the flag was not
    /// supplied. Resolution is the caller's job (see <see cref="ResolveAggregateType"/>) because it
    /// can fail and the failure is an input error, not a query result.
    /// </param>
    public IQueryable<StreamState> ApplyFilters(IQueryable<StreamState> queryable, Type? aggregateType)
    {
        if (aggregateType != null)
        {
            // The compaction-policy selector form, and the exact shape the compliance suite pins:
            // equality against a typeof constant, translated by the provider to the stored
            // aggregate-type identity.
            queryable = queryable.Where(x => x.AggregateType == aggregateType);
        }

        if (MinVersionFlag is { } minVersion)
        {
            queryable = queryable.Where(x => x.Version >= minVersion);
        }

        if (VersionAboveCompactedFlag is { } growth)
        {
            queryable = queryable.Where(x => x.Version - x.CompactedVersion > growth);
        }

        if (ArchivedFlag is { } archived)
        {
            queryable = queryable.Where(x => x.IsArchived == archived);
        }

        if (CreatedFrom is { } createdFrom)
        {
            queryable = queryable.Where(x => x.Created >= createdFrom);
        }

        if (CreatedTo is { } createdTo)
        {
            queryable = queryable.Where(x => x.Created <= createdTo);
        }

        if (UpdatedFrom is { } updatedFrom)
        {
            queryable = queryable.Where(x => x.LastTimestamp >= updatedFrom);
        }

        if (UpdatedTo is { } updatedTo)
        {
            queryable = queryable.Where(x => x.LastTimestamp <= updatedTo);
        }

        return queryable;
    }

    /// <summary>
    /// The command's stated ordering contract: creation order (oldest stream first), ties broken by
    /// stream identity so pages are deterministic. The identity member that does not apply to the
    /// store's identity style holds its default (<see cref="Guid.Empty"/> / null) on every row, so
    /// its tiebreak is a stable constant.
    /// </summary>
    public static IOrderedQueryable<StreamState> ApplyOrdering(IQueryable<StreamState> queryable)
        => queryable.OrderBy(x => x.Created).ThenBy(x => x.Id).ThenBy(x => x.Key);

    /// <summary>
    /// Resolve <c>--aggregate-type</c> to a CLR <see cref="Type"/> against the application's loaded
    /// assemblies, matching full name first and simple name second, case-insensitively. Ambiguity is
    /// refused rather than guessed — querying the wrong aggregate type would read exactly like an
    /// empty store.
    /// </summary>
    /// <remarks>
    /// Loaded-assembly scanning rather than a store registry because no shared surface exposes the
    /// store's aggregate-type graph (it hangs off each product's own options type). The command runs
    /// in-process on the application host, where building the store has already loaded the
    /// assemblies its aggregates live in.
    /// </remarks>
    public static (Type? Type, string? Error) ResolveAggregateType(string name)
        => ResolveAggregateType(name, AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic)
            .SelectMany(x =>
            {
                try
                {
                    return x.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    return e.Types.Where(t => t != null).Select(t => t!);
                }
            }));

    /// <summary>
    /// The testable core of <see cref="ResolveAggregateType(string)"/>: resolve against an explicit
    /// candidate set.
    /// </summary>
    public static (Type? Type, string? Error) ResolveAggregateType(string name, IEnumerable<Type> candidates)
    {
        var materialized = candidates.Where(x => x.IsClass && !x.IsAbstract).ToArray();

        var byFullName = materialized
            .Where(x => string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        if (byFullName.Length == 1)
        {
            return (byFullName[0], null);
        }

        var bySimpleName = materialized
            .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        return bySimpleName.Length switch
        {
            1 => (bySimpleName[0], null),
            0 => (null,
                $"No loaded type matches --aggregate-type {name}. Use the aggregate's simple or full CLR type name"),
            _ => (null,
                $"--aggregate-type {name} is ambiguous across: {string.Join(", ", bySimpleName.Select(x => x.FullName))}. Use the full type name")
        };
    }

    private static DateTimeOffset? parseTimestamp(string? flag)
        => flag.IsNotEmpty() && DateTimeOffset.TryParse(flag, out var parsed) ? parsed : null;
}
