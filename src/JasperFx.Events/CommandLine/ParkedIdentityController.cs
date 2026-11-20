using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// One parked identity in one database, for the <c>projections parked</c> listing (marten#6003).
/// </summary>
public record ParkedIdentityRow(Uri Store, string Database, ParkedIdentity Parked);

/// <summary>
/// What the parked action writes to, and asks, the operator. Split out so the action can be tested.
/// </summary>
public interface IParkedIdentityView
{
    void WriteParked(IReadOnlyList<ParkedIdentityRow> rows);
    void WriteMessage(string message);
    void WriteError(string message);

    /// <summary>
    /// Ask before discarding. Answer false when nobody can answer.
    /// </summary>
    bool Confirm(string question);
}

/// <summary>
/// <c>projections parked</c>: list the identities the daemon parked, with the error that parked each one, or ask
/// for a replay or a discard (marten#6003).
/// </summary>
/// <remarks>
/// <para>
/// A replay or discard is a durable request on the parked row. The node that runs the shard picks it up on its
/// heartbeat and does it in the shard's own batch, so this works from any process and never writes a parked
/// document itself. When no node runs the shard, the request waits until the shard next starts, and the command
/// says so.
/// </para>
/// </remarks>
public class ParkedIdentityController
{
    private readonly IReadOnlyList<IEventStore> _stores;
    private readonly IParkedIdentityView _view;

    public ParkedIdentityController(IReadOnlyList<IEventStore> stores, IParkedIdentityView view)
    {
        _stores = stores;
        _view = view;
    }

    /// <summary>How the command decides whether a shard is running somewhere. Replaced in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public async Task<bool> ExecuteAsync(ProjectionInput input, CancellationToken token = default)
    {
        if (input.ReplayFlag && input.DiscardFlag)
        {
            _view.WriteError("Use only one of --replay and --discard.");
            return false;
        }

        var writing = input.ReplayFlag || input.DiscardFlag;
        if (writing && input.ProjectionFlag.IsEmpty())
        {
            _view.WriteError("Name the projection with --projection.");
            return false;
        }

        if (input.DiscardFlag && input.IdentityFlag.IsEmpty())
        {
            _view.WriteError("Name the parked identity to discard with --identity. A discard is one identity at a time.");
            return false;
        }

        if (input.ReplayFlag)
        {
            var choices = new[] { !input.IdentityFlag.IsEmpty(), input.AllFlag, input.OldestFlag.HasValue }
                .Count(x => x);
            if (choices != 1)
            {
                _view.WriteError("With --replay, use exactly one of --identity <id>, --all and --oldest <n>.");
                return false;
            }

            if (input.OldestFlag is < 1)
            {
                _view.WriteError("--oldest takes a number of identities, at least 1.");
                return false;
            }
        }

        var targets = await targetsAsync(input, token).ConfigureAwait(false);
        if (targets == null) return false;

        if (targets.Count == 0)
        {
            _view.WriteMessage("No projections or databases match the selected filters.");
            return !writing;
        }

        var tenantId = input.TenantFlag.IsEmpty() ? null : input.TenantFlag;

        if (!writing)
        {
            var rows = new List<ParkedIdentityRow>();
            foreach (var target in targets)
            {
                var parked = await target.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery
                {
                    ProjectionName = target.Projection,
                    TenantId = tenantId,
                    Limit = 1000
                }, token).ConfigureAwait(false);

                rows.AddRange(parked.Select(x => new ParkedIdentityRow(target.Store, target.Database.Identifier, x)));
            }

            if (rows.Count == 0)
            {
                _view.WriteMessage("Nothing is parked.");
            }
            else
            {
                _view.WriteParked(rows);
            }

            return true;
        }

        return await requestAsync(input, targets, tenantId, token).ConfigureAwait(false);
    }

    private async Task<List<ParkedTarget>?> targetsAsync(ProjectionInput input, CancellationToken token)
    {
        var usages = new List<EventStoreUsage>();
        foreach (var store in _stores)
        {
            if (await store.TryCreateUsage(token).ConfigureAwait(false) is { } usage)
            {
                usages.Add(usage);
            }
        }

        var targets = new List<ParkedTarget>();
        foreach (var selection in ProjectionSelection.Filter(usages, input))
        {
            var store = _stores.FirstOrDefault(x => x.Subject == selection.Storage.SubjectUri);
            if (store == null) continue;

            var databases = await store.AllDatabases().ConfigureAwait(false);
            foreach (var identifier in selection.DatabaseIdentifiers)
            {
                var database = databases.FirstOrDefault(x => x.Identifier.EqualsIgnoreCase(identifier));
                if (database == null)
                {
                    _view.WriteError(
                        $"The event store '{store.Subject}' does not list database '{identifier}' in IEventStore.AllDatabases(), so its parked identities can't be reached.");
                    return null;
                }

                foreach (var subscription in selection.Subscriptions
                             .Where(x => x.Lifecycle == ProjectionLifecycle.Async))
                {
                    targets.Add(new ParkedTarget(store.Subject, database,
                        subscription.ShardNames.FirstOrDefault()?.Name ?? subscription.Name,
                        subscription.ShardNames));
                }
            }
        }

        return targets.DistinctBy(x => (x.Store, x.Database.Identifier, x.Projection)).ToList();
    }

    private async Task<bool> requestAsync(ProjectionInput input, IReadOnlyList<ParkedTarget> targets,
        string? tenantId, CancellationToken token)
    {
        var action = input.DiscardFlag ? ParkedAction.Discard : ParkedAction.Replay;
        var projection = targets[0].Projection;
        var what = input switch
        {
            { IdentityFlag: { Length: > 0 } identity } => $"parked identity '{identity}' of {projection}",
            { OldestFlag: { } oldest } => $"the {oldest} oldest parked identities of {projection}",
            _ => $"every parked identity of {projection}"
        };

        if (action == ParkedAction.Discard && !input.YesFlag &&
            !_view.Confirm($"Discard {what}? Each event that still fails is dead-lettered and the rest are applied."))
        {
            _view.WriteMessage("Nothing changed.");
            return false;
        }

        var marked = 0;
        foreach (var target in targets)
        {
            try
            {
                var count = await target.Database.RequestParkedActionAsync(new ParkedActionRequest
                {
                    ProjectionName = target.Projection,
                    Action = action,
                    TenantId = tenantId,
                    IdentityText = input.IdentityFlag.IsEmpty() ? null : input.IdentityFlag,
                    Oldest = input.OldestFlag
                }, token).ConfigureAwait(false);

                marked += count;
                if (count == 0) continue;

                _view.WriteMessage(
                    $"{target.Database.Identifier}: asked for a {action.ToString().ToLowerInvariant()} of {count} parked {(count == 1 ? "identity" : "identities")} of {target.Projection}.");

                if (!await isLiveAsync(target, token).ConfigureAwait(false))
                {
                    _view.WriteMessage(
                        $"No node is running {target.Projection} in {target.Database.Identifier} right now. The request runs when the shard next starts.");
                }
            }
            catch (NotSupportedException e)
            {
                _view.WriteError(e.Message);
                return false;
            }
        }

        if (marked == 0)
        {
            _view.WriteMessage($"Nothing matched: {what} is not parked.");
            return false;
        }

        if (input.WaitFlag is not > 0) return true;

        return await waitAsync(input, targets, tenantId, TimeSpan.FromSeconds(input.WaitFlag.Value), token)
            .ConfigureAwait(false);
    }

    private async Task<bool> isLiveAsync(ParkedTarget target, CancellationToken token)
    {
        foreach (var shard in target.Shards)
        {
            try
            {
                var progression = await target.Database.ReadProjectionProgressAsync(shard, token).ConfigureAwait(false);
                if (progression == null) continue;

                var state = new ShardState(shard, progression.Sequence)
                {
                    AgentStatus = progression.AgentStatus,
                    LastHeartbeat = progression.LastHeartbeat,
                    LastUpdated = progression.LastUpdated
                };

                if (IdentityRebuildGuard.IsLive(state, TimeProvider.GetUtcNow(), IdentityRebuildGuard.DefaultLiveWindow))
                {
                    return true;
                }
            }
            catch (NotSupportedException)
            {
                // A store that can't read one progression row can't say; don't claim the shard is down
                return true;
            }
        }

        return false;
    }

    private async Task<bool> waitAsync(ProjectionInput input, IReadOnlyList<ParkedTarget> targets, string? tenantId,
        TimeSpan timeout, CancellationToken token)
    {
        var deadline = TimeProvider.GetUtcNow().Add(timeout);
        while (true)
        {
            var remaining = 0;
            foreach (var target in targets)
            {
                var parked = await target.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery
                {
                    ProjectionName = target.Projection,
                    TenantId = tenantId,
                    Limit = 1000
                }, token).ConfigureAwait(false);

                remaining += parked.Count(x =>
                    x.RequestedAction.HasValue &&
                    (input.IdentityFlag.IsEmpty() || x.IdentityText == input.IdentityFlag));
            }

            if (remaining == 0)
            {
                _view.WriteMessage("Done: nothing requested is still waiting. Run 'projections parked' to see what failed again.");
                return true;
            }

            if (TimeProvider.GetUtcNow() > deadline)
            {
                _view.WriteError($"{remaining} requested {(remaining == 1 ? "identity is" : "identities are")} still waiting.");
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private record ParkedTarget(Uri Store, IEventDatabase Database, string Projection, ShardName[] Shards);
}

internal class ParkedIdentityView : IParkedIdentityView
{
    public void WriteParked(IReadOnlyList<ParkedIdentityRow> rows)
    {
        var table = new Table();
        table.AddColumn("Database");
        table.AddColumn("Projection");
        table.AddColumn("Tenant");
        table.AddColumn("Identity");
        table.AddColumn(new TableColumn("Failed At").RightAligned());
        table.AddColumn("Error");
        table.AddColumn("Parked");
        table.AddColumn("Replays");

        foreach (var row in rows)
        {
            var parked = row.Parked;
            var error = parked.LastAttemptError ?? parked.ExceptionMessage ?? "";
            table.AddRow(
                Markup.Escape(row.Database),
                Markup.Escape(parked.ProjectionName + (parked.ProjectionVersion > 1 ? $" V{parked.ProjectionVersion}" : "")),
                Markup.Escape(parked.TenantId),
                Markup.Escape(parked.IdentityText),
                parked.FirstFailingSequence.ToString(),
                Markup.Escape($"{parked.Category}: {error}"),
                Markup.Escape(parked.ParkedAt.ToString("u")),
                parked.RequestedAction is { } requested
                    ? $"{parked.ReplayAttempts} ({requested.ToString().ToLowerInvariant()} requested)"
                    : parked.ReplayAttempts.ToString());
        }

        AnsiConsole.Write(table);
    }

    public void WriteMessage(string message) => AnsiConsole.MarkupLine(Markup.Escape(message));

    public void WriteError(string message) => AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            WriteError("There's nobody to confirm this. Run it again with --yes.");
            return false;
        }

        return AnsiConsole.Confirm(Markup.Escape(question), defaultValue: false);
    }
}
