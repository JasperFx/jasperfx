using JasperFx.CommandLine;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Queries the streams table of the application's own event store through
/// <see cref="IReadOnlyEventStore.QueryStreamStates"/> (jasperfx#740) — the CLI face of the Stream
/// Compaction Policy questions: "every stream of aggregate type X whose un-compacted growth
/// exceeds N". Sits beside <c>event-query</c> and <c>projection-run</c> with the same honesty
/// rules: a filter the store cannot honor is refused by name, and an empty result with a truthful
/// total is a real answer.
/// </summary>
/// <remarks>
/// Ordering contract: streams are returned in creation order (oldest first), ties broken by stream
/// identity, so pages are deterministic — see <see cref="StreamQueryInput.ApplyOrdering"/>.
/// </remarks>
// Name is explicit because CommandFactory.CommandNameFor only strips the "Command" suffix and
// lowercases — it does not split PascalCase — so this would otherwise be "streamquery".
[Description("Query event stream metadata (version, aggregate type, compaction watermark, archive flag) with filters and paging",
    Name = "stream-query")]
public class StreamQueryCommand: JasperFxAsyncCommand<StreamQueryInput>
{
    public StreamQueryCommand()
    {
        Usage("Query the event streams of the application's event store");
    }

    public override async Task<bool> Execute(StreamQueryInput input)
    {
        // Console logging has to go: in the default JSON mode a single stray log line makes the
        // output unparseable. --verbose opts back in, but only for --format text, matching the
        // sibling commands.
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
            return fail(input, null, null, validation);
        }

        using var host = input.BuildHost();

        var stores = host.Services.GetServices<IEventStore>().ToArray();
        var (store, storeError) = ProjectionRunSource.SelectStore(stores, input.StoreFlag);
        if (store == null)
        {
            return fail(input, null, null, storeError!);
        }

        // Resolved after the host is built: building the store is what loads the assemblies the
        // application's aggregates live in.
        Type? aggregateType = null;
        if (input.AggregateTypeFlag != null)
        {
            (aggregateType, var typeError) = StreamQueryInput.ResolveAggregateType(input.AggregateTypeFlag);
            if (aggregateType == null)
            {
                return fail(input, null, store.Subject, typeError!);
            }
        }

        IReadOnlyList<StreamState> page;
        int totalCount;
        try
        {
            var filtered = input.ApplyFilters(
                store.OpenReadOnlyEventStore().QueryStreamStates(input.TenantFlag), aggregateType);

            totalCount = await filtered.CountAsync(CancellationToken.None).ConfigureAwait(false);

            page = await StreamQueryInput.ApplyOrdering(filtered)
                .Skip((input.PageFlag - 1) * input.PageSizeFlag)
                .Take(input.PageSizeFlag)
                .ToListAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NotSupportedException e)
        {
            // A tenant on a store with no tenant dimension, or a StreamState member the provider
            // cannot translate, refusing by name per the jasperfx#737 rule. Say so plainly — the
            // one thing this must never become is an empty result.
            return fail(input, aggregateType, store.Subject,
                $"This event store does not support part of the query: {e.Message}");
        }
        catch (Exception e)
        {
            return await failAsync(host, input, aggregateType, store.Subject, e.Message).ConfigureAwait(false);
        }

        write(input, StreamQueryReport.From(input, aggregateType, store.Subject, page, totalCount));

        // An empty page with TotalCount 0 lands here: a real answer, reported as one.
        return true;
    }

    /// <summary>
    /// A failed run still writes a report, so JSON consumers parse one shape either way rather
    /// than having to tell an empty result from a crash.
    /// </summary>
    private static bool fail(StreamQueryInput input, Type? aggregateType, Uri? store, string error)
    {
        write(input, StreamQueryReport.Failed(input, aggregateType, store, error));
        return false;
    }

    /// <summary>
    /// The same, for failures against a built host — worth asking whether the application's
    /// storage was ever migrated, exactly as the sibling commands do.
    /// </summary>
    private static async Task<bool> failAsync(IHost host, StreamQueryInput input, Type? aggregateType,
        Uri? store, string error)
    {
        var diagnosis = await ProjectionRunSchemaDiagnosis
            .TryDiagnoseAsync(host.Services, CancellationToken.None).ConfigureAwait(false);

        write(input, StreamQueryReport.Failed(input, aggregateType, store, error, diagnosis));
        return false;
    }

    private static void write(StreamQueryInput input, StreamQueryReport report)
    {
        if (input.FormatFlag == EventQueryFormat.Json)
        {
            StreamQueryView.WriteJson(report);
        }
        else
        {
            StreamQueryView.WriteConsole(report);
        }
    }
}
