using JasperFx.Core;
using JasperFx.Descriptors;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Result of reading the source slice for a <c>projection-run</c>. <see cref="Truncated"/> is true
/// when the read stopped at <c>--max-events</c> rather than at the end of the slice — the caller
/// must say so, because a silently capped read produces a timeline that looks complete and is not.
/// </summary>
/// <param name="Events">Source events in apply order.</param>
/// <param name="Truncated">True when <c>--max-events</c> stopped the read early.</param>
internal sealed record ProjectionRunSourceEvents(IReadOnlyList<EventRecord> Events, bool Truncated);

/// <summary>
/// Resolves the store and reads the source slice for the <c>projection-run</c> command. Split out
/// from the command itself so both halves are testable against a fake <see cref="IEventStore"/>
/// without a database or a built host.
/// </summary>
internal static class ProjectionRunSource
{
    /// <summary>
    /// Pick the one event store this run targets. Matching mirrors <see cref="ProjectionSelection"/>:
    /// an absolute uri matches by subject, anything else matches by scheme. Ambiguity is an error
    /// rather than a first-match guess — replaying against the wrong store would look like a
    /// projection bug.
    /// </summary>
    public static (IEventStore? Store, string? Error) SelectStore(
        IReadOnlyList<IEventStore> stores, string? storeFlag)
    {
        if (stores.Count == 0)
        {
            return (null, "No event stores are registered in this application");
        }

        if (storeFlag.IsEmpty())
        {
            return stores.Count == 1
                ? (stores[0], null)
                : (null, $"This application registers more than one event store; specify --store. Known stores: {describe(stores)}");
        }

        var matches = Uri.TryCreate(storeFlag, UriKind.Absolute, out var subjectUri)
            ? stores.Where(x => x.Subject.Matches(subjectUri!)).ToArray()
            : stores.Where(x => x.Subject.Scheme.EqualsIgnoreCase(storeFlag)).ToArray();

        return matches.Length switch
        {
            1 => (matches[0], null),
            0 => (null, $"No event store matches --store {storeFlag}. Known stores: {describe(stores)}"),
            _ => (null, $"--store {storeFlag} matches more than one event store: {describe(matches)}")
        };
    }

    /// <summary>
    /// Read the source events for the input's mode. The tenant-scoped read overloads are always the
    /// ones called: a null tenant delegates to the store-global member by contract (jasperfx#503/#555),
    /// so there is no second code path to keep in step.
    /// </summary>
    public static async Task<ProjectionRunSourceEvents> ReadAsync(
        IEventStore store, ProjectionRunInput input, CancellationToken ct)
    {
        var events = new List<EventRecord>();
        var cap = input.MaxEventsFlag;

        switch (input.SourceMode)
        {
            case ProjectionRunSourceMode.TagQuery:
                await foreach (var e in store.QueryByTagsAsync(input.TagFlag, input.TenantFlag, ct).ConfigureAwait(false))
                {
                    if (events.Count == cap) return new ProjectionRunSourceEvents(events, true);
                    events.Add(e);
                }

                break;

            case ProjectionRunSourceMode.StreamSlice:
            {
                // The JasperFx.Events abstraction still only ships a full-stream read, so the version
                // bounds are applied here. Same in-process filter CritterWatch's handler carries, and
                // it can leave early because a stream read is ordered by version.
                var from = input.FromFlag!.Value;
                var to = input.ToFlag!.Value;

                await foreach (var e in store.ReadStreamAsync(input.StreamFlag!, input.TenantFlag, ct).ConfigureAwait(false))
                {
                    if (e.StreamVersion < from) continue;
                    if (e.StreamVersion > to) break;
                    if (events.Count == cap) return new ProjectionRunSourceEvents(events, true);
                    events.Add(e);
                }

                break;
            }

            default:
                await foreach (var e in store.ReadStreamAsync(input.StreamFlag!, input.TenantFlag, ct).ConfigureAwait(false))
                {
                    if (events.Count == cap) return new ProjectionRunSourceEvents(events, true);
                    events.Add(e);
                }

                break;
        }

        return new ProjectionRunSourceEvents(events, false);
    }

    private static string describe(IReadOnlyList<IEventStore> stores)
        => string.Join(", ", stores.Select(x => x.Subject.ToString()));
}
