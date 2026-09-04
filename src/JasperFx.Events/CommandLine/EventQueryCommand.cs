using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Queries events across all streams of the application's own event store through the broadened
/// <see cref="EventQuery"/> surface (jasperfx#737) — every filter field, sequence-ascending
/// ordering, offset paging. The local, in-process twin of CritterWatch's <c>query_events</c> MCP
/// tool, the same relationship <c>projection-run</c> has to the console's stepper (jasperfx#728):
/// both sit on <see cref="IReadOnlyEventStore.QueryEventsAsync"/>, so an operator or agent that
/// learns one has learned the other.
/// </summary>
// Name is explicit because CommandFactory.CommandNameFor only strips the "Command" suffix and
// lowercases — it does not split PascalCase — so this would otherwise be "eventquery".
[Description("Query events across all streams with filters, ordered by sequence ascending, paged",
    Name = "event-query")]
public class EventQueryCommand: JasperFxAsyncCommand<EventQueryInput>
{
    public EventQueryCommand()
    {
        Usage("Query events across all streams of the application's event store");
    }

    public override async Task<bool> Execute(EventQueryInput input)
    {
        // Console logging has to go: in the default JSON mode a single stray log line makes the
        // output unparseable. --verbose opts back in, but only for --format text, matching
        // projection-run's disposition.
        if (input.HostBuilder != null && (!input.VerboseFlag || input.FormatFlag == EventQueryFormat.Json))
        {
            input.HostBuilder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
        }

        var validation = input.Validate();
        if (validation != null)
        {
            return fail(input, null, validation);
        }

        var query = input.BuildQuery();

        using var host = input.BuildHost();

        var stores = host.Services.GetServices<IEventStore>().ToArray();
        var (store, storeError) = ProjectionRunSource.SelectStore(stores, input.StoreFlag);
        if (store == null)
        {
            return fail(input, null, storeError!);
        }

        PagedEvents page;
        try
        {
            page = await store.OpenReadOnlyEventStore()
                .QueryEventsAsync(query, CancellationToken.None).ConfigureAwait(false);
        }
        catch (NotSupportedException e)
        {
            // The jasperfx#737 guard rail (or a tenant filter on a store with no tenant dimension)
            // refusing a filter it does not honor. Say so plainly — the one thing this must never
            // become is an empty result.
            return fail(input, store.Subject,
                $"This event store does not support part of the query: {e.Message}");
        }
        catch (Exception e)
        {
            return await failAsync(host, input, store.Subject, e.Message).ConfigureAwait(false);
        }

        write(input, EventQueryReport.From(query, store.Subject, page, includePayloads: !input.NoPayloadsFlag));

        // An empty page with TotalCount 0 lands here: a real answer, reported as one.
        return true;
    }

    /// <summary>
    /// A failed run still writes a report, so JSON consumers parse one shape either way rather
    /// than having to tell an empty result from a crash.
    /// </summary>
    private static bool fail(EventQueryInput input, Uri? store, string error)
    {
        write(input, EventQueryReport.Failed(input.BuildQuery(), store, error));
        return false;
    }

    /// <summary>
    /// The same, for failures against a built host — worth asking whether the application's
    /// storage was ever migrated, exactly as <c>projection-run</c> does: the command never starts
    /// the host, so an un-migrated database is a first-class cause and the raw store exception
    /// ("relation ... does not exist") says nothing about the fix.
    /// </summary>
    private static async Task<bool> failAsync(IHost host, EventQueryInput input, Uri? store, string error)
    {
        var diagnosis = await ProjectionRunSchemaDiagnosis
            .TryDiagnoseAsync(host.Services, CancellationToken.None).ConfigureAwait(false);

        write(input, EventQueryReport.Failed(input.BuildQuery(), store, error, diagnosis));
        return false;
    }

    private static void write(EventQueryInput input, EventQueryReport report)
    {
        if (input.FormatFlag == EventQueryFormat.Json)
        {
            EventQueryView.WriteJson(report);
        }
        else
        {
            EventQueryView.WriteConsole(report);
        }
    }
}
