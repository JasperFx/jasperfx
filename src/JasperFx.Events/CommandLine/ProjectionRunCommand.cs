using JasperFx.CommandLine;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Replays one projection over a source slice and prints the per-event before/after state — the
/// projection debugger every Marten / Polecat / Fisher application gets without installing a
/// monitoring console (jasperfx#728). Stateless: nothing is written, and the projection's own
/// stored state is never touched.
/// </summary>
/// <remarks>
/// This is the local, in-process half of the same capability CritterWatch exposes remotely over
/// its stepper. Both sit on <see cref="IEventStore.RunMultiStreamProjectionAsync"/>, and the
/// per-mode validation rules are deliberately identical, so an operator who learns one has learned
/// the other.
/// </remarks>
[Description("Replay a projection over a stream, stream slice, or tag query and show the per-event before/after state")]
public class ProjectionRunCommand: JasperFxAsyncCommand<ProjectionRunInput>
{
    public ProjectionRunCommand()
    {
        Usage("Replay a projection over a source slice").Arguments(x => x.ProjectionName);
    }

    public override async Task<bool> Execute(ProjectionRunInput input)
    {
        // Console logging has to go: in --json mode a single stray log line makes the output
        // unparseable, and even the table is unreadable underneath startup chatter. --verbose is
        // the deliberate opt back in, and it is refused in --json mode for the same reason.
        if (input.HostBuilder != null && (!input.VerboseFlag || input.JsonFlag))
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

        using var host = input.BuildHost();

        var stores = host.Services.GetServices<IEventStore>().ToArray();
        var (store, storeError) = ProjectionRunSource.SelectStore(stores, input.StoreFlag);
        if (store == null)
        {
            return fail(input, null, storeError!);
        }

        ProjectionRunSourceEvents source;
        try
        {
            source = await ProjectionRunSource.ReadAsync(store, input, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return fail(input, store.Subject, $"Unable to read the source events: {e.Message}");
        }

        MultiAggregateProjectionResult result;
        try
        {
            result = await store
                .RunMultiStreamProjectionAsync(input.ProjectionName, source.Events, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return fail(input, store.Subject, e.Message);
        }

        var report = ProjectionRunReport.From(input, store.Subject, result, source.Events.Count, source.Truncated);
        write(input, report);

        return true;
    }

    /// <summary>
    /// A failed run still writes a report, so <c>--json</c> consumers parse one shape either way
    /// rather than having to tell an empty result from a crash.
    /// </summary>
    private static bool fail(ProjectionRunInput input, Uri? store, string error)
    {
        write(input, ProjectionRunReport.Failed(input, store, error));
        return false;
    }

    private static void write(ProjectionRunInput input, ProjectionRunReport report)
    {
        if (input.JsonFlag)
        {
            ProjectionRunView.WriteJson(report);
        }
        else
        {
            ProjectionRunView.WriteConsole(report);
        }
    }
}
