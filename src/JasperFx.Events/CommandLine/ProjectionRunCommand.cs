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
// Name is explicit because CommandFactory.CommandNameFor only strips the "Command" suffix and
// lowercases — it does not split PascalCase — so this would otherwise be "projectionrun".
[Description("Replay a projection over a stream, stream slice, or tag query and show the per-event before/after state",
    Name = "projection-run")]
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
            return await failAsync(host, input, store.Subject, $"Unable to read the source events: {e.Message}")
                .ConfigureAwait(false);
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
            return await failAsync(host, input, store.Subject, e.Message).ConfigureAwait(false);
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

    /// <summary>
    /// The same, for failures that happened against a built host — those are the ones worth asking
    /// whether the application's storage was ever migrated. The command builds the host but never
    /// starts it, so an un-migrated database is a first-class cause here rather than an exotic one,
    /// and the raw store exception ("relation ... does not exist") says nothing about the fix.
    /// </summary>
    private static async Task<bool> failAsync(IHost host, ProjectionRunInput input, Uri? store, string error)
    {
        var diagnosis = await ProjectionRunSchemaDiagnosis
            .TryDiagnoseAsync(host.Services, CancellationToken.None).ConfigureAwait(false);

        write(input, ProjectionRunReport.Failed(input, store, error, diagnosis));
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
