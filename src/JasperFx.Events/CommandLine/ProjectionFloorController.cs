using JasperFx.Core;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// One projection's stored start floor in one database, for one tenant or store-global (jasperfx#1205).
/// </summary>
/// <param name="Store">The event store's subject.</param>
/// <param name="Database">The database identifier.</param>
/// <param name="Projection">The projection name the floor is keyed by, without its version.</param>
/// <param name="TenantId">The tenant under tenant-partitioned events, or null for store-global.</param>
/// <param name="Floor">The stored floor, or null when none is stored.</param>
/// <param name="Unsupported">True when this store does not implement the floor seam.</param>
public record ProjectionFloorRow(Uri Store, string Database, string Projection, string? TenantId, long? Floor,
    bool Unsupported = false);

/// <summary>
/// What the floor action writes to, and asks, the operator. Split out so the action can be tested.
/// </summary>
public interface IProjectionFloorView
{
    void WriteFloors(IReadOnlyList<ProjectionFloorRow> rows);
    void WriteMessage(string message);
    void WriteError(string message);

    /// <summary>
    /// Ask before changing a floor. Answer false when nobody can answer.
    /// </summary>
    bool Confirm(string question);
}

/// <summary>
/// <c>projections floor</c>: see, pin, set or clear a projection's persisted start floor without SQL
/// (jasperfx#1205), through the <see cref="IEventDatabase" /> floor seam, so it works for every store that
/// implements it.
/// </summary>
/// <remarks>
/// <para>
/// A floor is keyed by projection name without the version, and by tenant under tenant-partitioned events.
/// <c>--tenant</c> picks that tenant's floor (and, as for the other actions, the database holding the
/// tenant). Leave it off for the store-global floor, which is also what a database-per-tenant store uses.
/// </para>
/// <para>
/// Writes need one projection named with <c>--projection</c>, apply to every selected database, and ask
/// before writing unless <c>--yes</c> is given. Nothing is replayed by a write: the next continuous start or
/// rebuild honours the new floor.
/// </para>
/// </remarks>
public class ProjectionFloorController
{
    private readonly IReadOnlyList<IEventStore> _stores;
    private readonly IProjectionFloorView _view;

    public ProjectionFloorController(IReadOnlyList<IEventStore> stores, IProjectionFloorView view)
    {
        _stores = stores;
        _view = view;
    }

    public async Task<bool> ExecuteAsync(ProjectionInput input, CancellationToken token = default)
    {
        var modes = new[] { input.ShowFlag, input.PinCurrentFlag, input.SetFlag.HasValue, input.ClearFlag }
            .Count(x => x);
        if (modes > 1)
        {
            _view.WriteError("Use only one of --show, --pin-current, --set and --clear.");
            return false;
        }

        if (input.SetFlag is < 0)
        {
            _view.WriteError("A floor is an event sequence, so it can't be negative.");
            return false;
        }

        var writing = input.PinCurrentFlag || input.SetFlag.HasValue || input.ClearFlag;
        if (writing && input.ProjectionFlag.IsEmpty())
        {
            _view.WriteError("Name the projection whose floor to change with --projection.");
            return false;
        }

        var usages = new List<EventStoreUsage>();
        foreach (var store in _stores)
        {
            if (await store.TryCreateUsage(token).ConfigureAwait(false) is { } usage)
            {
                usages.Add(usage);
            }
        }

        if (usages.Count == 0)
        {
            _view.WriteMessage("No event stores in this application.");
            return !writing;
        }

        var selections = ProjectionSelection.Filter(usages, input);
        var targets = new List<FloorTarget>();
        foreach (var selection in selections)
        {
            var store = _stores.FirstOrDefault(x => x.Subject == selection.Storage.SubjectUri);
            if (store == null) continue;

            var databases = await store.AllDatabases().ConfigureAwait(false);
            foreach (var identifier in selection.DatabaseIdentifiers)
            {
                var database = databases.FirstOrDefault(x => x.Identifier.EqualsIgnoreCase(identifier));
                if (database == null)
                {
                    // An operator action that silently did nothing is worse than one that refuses
                    _view.WriteError(
                        $"The event store '{store.Subject}' does not list database '{identifier}' in IEventStore.AllDatabases(), so its floors can't be reached.");
                    return false;
                }

                foreach (var subscription in selection.Subscriptions
                             .Where(x => x.Lifecycle != ProjectionLifecycle.Live))
                {
                    targets.Add(new FloorTarget(store.Subject, database, floorNameFor(subscription)));
                }
            }
        }

        targets = targets.DistinctBy(x => (x.Store, x.Database.Identifier, x.Projection)).ToList();

        if (targets.Count == 0)
        {
            _view.WriteMessage("No projections or databases match the selected filters.");
            return !writing;
        }

        var tenantId = input.TenantFlag.IsEmpty() ? null : input.TenantFlag;

        if (!writing)
        {
            _view.WriteFloors(await readAsync(targets, tenantId, token).ConfigureAwait(false));
            return true;
        }

        return await writeAsync(input, targets, tenantId, token).ConfigureAwait(false);
    }

    // The floor is keyed by ShardName.Name, the projection name without its version
    private static string floorNameFor(SubscriptionDescriptor subscription)
        => subscription.ShardNames.FirstOrDefault()?.Name ?? subscription.Name;

    private static async Task<IReadOnlyList<ProjectionFloorRow>> readAsync(IReadOnlyList<FloorTarget> targets,
        string? tenantId, CancellationToken token)
    {
        var rows = new List<ProjectionFloorRow>();
        foreach (var target in targets)
        {
            try
            {
                var floor = await target.Database.FetchProjectionFloorAsync(target.Projection, tenantId, token)
                    .ConfigureAwait(false);
                rows.Add(new ProjectionFloorRow(target.Store, target.Database.Identifier, target.Projection, tenantId,
                    floor));
            }
            catch (NotSupportedException)
            {
                rows.Add(new ProjectionFloorRow(target.Store, target.Database.Identifier, target.Projection, tenantId,
                    null, Unsupported: true));
            }
        }

        return rows;
    }

    private async Task<bool> writeAsync(ProjectionInput input, IReadOnlyList<FloorTarget> targets, string? tenantId,
        CancellationToken token)
    {
        var projection = targets[0].Projection;
        var where = targets.Select(x => x.Database.Identifier).Distinct().Join(", ");
        var whose = tenantId == null ? "" : $" for tenant '{tenantId}'";

        var description = input switch
        {
            { PinCurrentFlag: true } => $"Set the start floor of {projection}{whose} to the current high water mark in {where}?",
            { SetFlag: { } value } => $"Set the start floor of {projection}{whose} to {value} in {where}?",
            _ => $"Clear the start floor of {projection}{whose} in {where}? The next rebuild replays from 0, or from the floor configured in code."
        };

        if (!input.YesFlag && !_view.Confirm(description))
        {
            _view.WriteMessage("Nothing changed.");
            return false;
        }

        foreach (var target in targets)
        {
            try
            {
                long? floor = input switch
                {
                    { PinCurrentFlag: true } => await target.Database.FetchHighestEventSequenceNumber(tenantId, token)
                        .ConfigureAwait(false),
                    { SetFlag: { } value } => value,
                    _ => null
                };

                await target.Database.SetProjectionFloorAsync(target.Projection, tenantId, floor, token)
                    .ConfigureAwait(false);

                _view.WriteMessage(floor is { } set
                    ? $"{target.Projection}{whose} in {target.Database.Identifier}: start floor is now {set}."
                    : $"{target.Projection}{whose} in {target.Database.Identifier}: start floor cleared.");
            }
            catch (NotSupportedException e)
            {
                _view.WriteError(e.Message);
                return false;
            }
        }

        _view.WriteMessage("Nothing is replayed now. The next continuous start or rebuild uses the new floor.");
        return true;
    }

    private record FloorTarget(Uri Store, IEventDatabase Database, string Projection);
}

internal class ProjectionFloorView : IProjectionFloorView
{
    public void WriteFloors(IReadOnlyList<ProjectionFloorRow> rows)
    {
        var table = new Table();
        table.AddColumn("Store");
        table.AddColumn("Database");
        table.AddColumn("Projection");
        table.AddColumn("Tenant");
        table.AddColumn(new TableColumn("Start Floor").RightAligned());

        foreach (var row in rows)
        {
            var floor = row.Unsupported
                ? "[gray]not supported by this store[/]"
                : row.Floor?.ToString() ?? "[gray]none[/]";

            table.AddRow(Markup.Escape(row.Store.ToString()), Markup.Escape(row.Database),
                Markup.Escape(row.Projection), Markup.Escape(row.TenantId ?? "(store-global)"), floor);
        }

        AnsiConsole.Write(table);
    }

    public void WriteMessage(string message) => AnsiConsole.MarkupLine(Markup.Escape(message));

    public void WriteError(string message) => AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            WriteError("There's nobody to confirm this change. Run it again with --yes.");
            return false;
        }

        return AnsiConsole.Confirm(Markup.Escape(question), defaultValue: false);
    }
}
