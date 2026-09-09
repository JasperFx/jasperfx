using System.Text.Json;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Tags;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Output rendering for the <c>event-query</c> command. JSON is the default deliberately — the
/// command is agent-first, the same disposition as the CritterWatch MCP tool it twins.
/// </summary>
public enum EventQueryFormat
{
    Json,
    Text
}

/// <summary>
/// Input for the <c>event-query</c> command. Every flag is long-form only for the same reason as
/// <see cref="ProjectionRunInput"/>: the parser derives one-letter aliases from the first letter
/// with no collision detection, and this surface has <c>--stream</c>/<c>--store</c>/<c>--sequence-*</c>,
/// <c>--timestamp-*</c>/<c>--tenant</c>/<c>--tags</c> all competing for the same letters.
/// </summary>
public class EventQueryInput: NetCoreInput
{
    [Description("Stream id (string form) whose events to return. Omit for a cross-stream query")]
    [FlagAlias("stream", longAliasOnly: true)]
    public string? StreamFlag { get; set; }

    [Description("Event type alias(es) to match, comma-separated for more than one (exact match)")]
    [FlagAlias("event-type", longAliasOnly: true)]
    public string? EventTypeFlag { get; set; }

    [Description("Exact-match filter on the correlation id metadata column")]
    [FlagAlias("correlation-id", longAliasOnly: true)]
    public string? CorrelationIdFlag { get; set; }

    [Description("Exact-match filter on the causation id metadata column")]
    [FlagAlias("causation-id", longAliasOnly: true)]
    public string? CausationIdFlag { get; set; }

    [Description("Exact-match filter on the user name metadata column")]
    [FlagAlias("user-name", longAliasOnly: true)]
    public string? UserNameFlag { get; set; }

    [Description("Inclusive lower bound on the event timestamp, any DateTimeOffset format (e.g. 2026-09-01T00:00:00Z)")]
    [FlagAlias("timestamp-from", longAliasOnly: true)]
    public string? TimestampFromFlag { get; set; }

    [Description("Inclusive upper bound on the event timestamp, any DateTimeOffset format")]
    [FlagAlias("timestamp-to", longAliasOnly: true)]
    public string? TimestampToFlag { get; set; }

    [Description("Inclusive lower bound on the store-global event sequence")]
    [FlagAlias("sequence-floor", longAliasOnly: true)]
    public long? SequenceFloorFlag { get; set; }

    [Description("Inclusive upper bound on the store-global event sequence")]
    [FlagAlias("sequence-ceiling", longAliasOnly: true)]
    public long? SequenceCeilingFlag { get; set; }

    [Description("Tag filter as a JSON object of tag name to tag value, e.g. '{\"StudentId\":\"s-1\"}'. A name is the tag type's name or its registered table suffix; entries are AND'd, then AND-combined with the other filters")]
    [FlagAlias("tags", longAliasOnly: true)]
    public string? TagsFlag { get; set; }

    [Description("Tenant partition to scope the query to. Omit for a store-global read")]
    [FlagAlias("tenant", longAliasOnly: true)]
    public string? TenantFlag { get; set; }

    [Description("1-based page number into the sequence-ascending ordering. Default is 1")]
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

    [Description("Omit the event payloads from the output, leaving only the envelope metadata")]
    [FlagAlias("no-payloads", longAliasOnly: true)]
    public bool NoPayloadsFlag { get; set; }

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

        if (TimestampFromFlag.IsNotEmpty() && !DateTimeOffset.TryParse(TimestampFromFlag, out _))
        {
            return $"--timestamp-from '{TimestampFromFlag}' is not a recognizable timestamp";
        }

        if (TimestampToFlag.IsNotEmpty() && !DateTimeOffset.TryParse(TimestampToFlag, out _))
        {
            return $"--timestamp-to '{TimestampToFlag}' is not a recognizable timestamp";
        }

        if (parseTimestamp(TimestampFromFlag) is { } from && parseTimestamp(TimestampToFlag) is { } to && from > to)
        {
            return "--timestamp-from must be less than or equal to --timestamp-to";
        }

        if (SequenceFloorFlag.HasValue && SequenceCeilingFlag.HasValue && SequenceFloorFlag > SequenceCeilingFlag)
        {
            return "--sequence-floor must be less than or equal to --sequence-ceiling";
        }

        var (_, tagError) = TryParseTags(TagsFlag);
        return tagError;
    }

    /// <summary>
    /// The <see cref="EventQuery"/> these flags describe. Call after <see cref="Validate"/> has
    /// returned null; an invalid member is dropped rather than guessed at here.
    /// </summary>
    public EventQuery BuildQuery()
    {
        var query = new EventQuery
        {
            StreamId = StreamFlag,
            CorrelationId = CorrelationIdFlag,
            CausationId = CausationIdFlag,
            UserName = UserNameFlag,
            TenantId = TenantFlag,
            TimestampFrom = parseTimestamp(TimestampFromFlag),
            TimestampTo = parseTimestamp(TimestampToFlag),
            SequenceFloor = SequenceFloorFlag,
            SequenceCeiling = SequenceCeilingFlag,
            PageNumber = PageFlag,
            PageSize = PageSizeFlag
        };

        if (TryParseTags(TagsFlag).Tags is { } tags)
        {
            query.TagValues = tags;
        }

        if (EventTypeFlag.IsNotEmpty())
        {
            // Always the list form, even for one alias — CombinedEventTypeNames() folds the two
            // spellings together on the store side, so the CLI never needs the single-name member.
            query.EventTypeNames = EventTypeFlag!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return query;
    }

    /// <summary>
    /// Parse the <c>--tags</c> JSON object into <see cref="EventQuery.TagValues"/> — the lossy
    /// name/value tag filter (jasperfx#801). Each property is one entry: the name as the store's
    /// registered tag graph knows it (the tag type's name or its registered table suffix), the value
    /// rendered as text for the store to match against the stored tag value's string form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the lossy form rather than the rich <see cref="EventTagQuerySpec"/> one, because the
    /// rich form's tag <em>types</em> can only be resolved against the store's registered graph, and
    /// the CLI has a name. Building a spec here meant manufacturing a
    /// <see cref="TypeDescriptor"/> whose <c>FullName</c> was the bare name, which
    /// <see cref="EventTagQuerySpec.ResolverFor"/> — a full-name match — could never resolve for a
    /// tag type in a namespace. See jasperfx#803.
    /// </para>
    /// <para>
    /// Entries are therefore AND'd rather than OR'd, which is also what an operator naming two tags
    /// means, and what the store's own dictionary tag query has always done.
    /// </para>
    /// </remarks>
    /// <returns>The tag entries, or the operator-facing error. A missing flag is (null, null): no tag filter.</returns>
    public static (Dictionary<string, string>? Tags, string? Error) TryParseTags(string? json)
    {
        if (json.IsEmpty())
        {
            return (null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json!);
        }
        catch (JsonException e)
        {
            return (null, $"--tags is not valid JSON: {e.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "--tags must be a JSON object of tag name to tag value, e.g. '{\"StudentId\":\"s-1\"}'");
            }

            var tags = new Dictionary<string, string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    return (null, $"--tags value for '{property.Name}' is null; a tag filter needs a value");
                }

                // A tag value is always a strong-typed id over a primitive -- TagTypeRegistration
                // requires a simple inner type -- so a composite has no faithful text form and is a
                // mangled invocation rather than a query. Refused rather than stringified, because
                // a stringified object would match nothing and read as "no such events".
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return (null,
                        $"--tags value for '{property.Name}' is a JSON {property.Value.ValueKind.ToString().ToLowerInvariant()}; " +
                        "a tag value is the id itself, written as a string, number or boolean");
                }

                tags[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    // Numbers and booleans keep their literal spelling; the store matches on the
                    // stored value's string form.
                    : property.Value.GetRawText();
            }

            if (tags.Count == 0)
            {
                // An empty object filters nothing, and a tag filter that filters nothing is far more
                // likely a mangled invocation than an intent — refuse it rather than quietly running
                // the query unfiltered.
                return (null, "--tags is an empty JSON object; supply at least one tag filter or omit the flag");
            }

            return (tags, null);
        }
    }

    private static DateTimeOffset? parseTimestamp(string? flag)
        => flag.IsNotEmpty() && DateTimeOffset.TryParse(flag, out var parsed) ? parsed : null;
}
