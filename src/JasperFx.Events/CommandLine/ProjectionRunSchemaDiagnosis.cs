using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Works out whether a failed <c>projection-run</c> failed because the application's storage was
/// never migrated, and says what to run about it.
/// </summary>
/// <remarks>
/// <para>
/// The command builds the host but does not start it, so nothing that normally migrates on startup
/// has run — no <c>AddResourceSetupOnStartup</c>, no hosted service. Whether that matters depends on
/// the store: Marten creates document and event tables on demand under the default
/// <c>AutoCreate.CreateOrUpdate</c>, Polecat creates document tables on demand but takes its event
/// tables from the resource model, and any deployment on <c>AutoCreate.None</c> creates nothing at
/// all. So the same command can succeed on one developer's box and fail on the next with a raw
/// "relation does not exist" that says nothing about what to do.
/// </para>
/// <para>
/// The diagnosis runs on the FAILURE path only. Running it up front would cost a round trip on every
/// successful run, and would report a broker that happens to be down as though it were the reason a
/// projection could not be replayed.
/// </para>
/// </remarks>
internal static class ProjectionRunSchemaDiagnosis
{
    /// <summary>
    /// A resource check is a network call. It is diagnosing a failure that already happened, so it
    /// gets a short leash — a hung check must never be more expensive than the error it explains.
    /// </summary>
    public static TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns operator-facing guidance when the application's resources are not ready, or null when
    /// they are fine and the failure was something else. Never throws: a diagnosis that fails is
    /// simply absent, and the original error is always what gets reported.
    /// </summary>
    public static async Task<string?> TryDiagnoseAsync(IServiceProvider services, CancellationToken ct)
    {
        List<string> unhealthy;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);

            unhealthy = await findUnhealthyResourcesAsync(services, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Diagnosing is best-effort by construction. The caller already has a real error to
            // report and it must not be replaced by a failure to explain it.
            return null;
        }

        if (unhealthy.Count == 0) return null;

        return $"""
                This application's storage is not ready — 'resources check' fails for: {string.Join(", ", unhealthy)}.
                That is the usual reason a projection cannot be replayed: projection-run builds the host but
                never starts it, so nothing that normally migrates on startup has run.

                projection-run does not migrate anything itself, deliberately: a read-only diagnostic that
                quietly changes a database is worse than one that fails. Apply the schema first, then re-run.

                  dotnet run -- resources setup   (every registered resource; works on every store)
                  dotnet run -- db-apply          (Weasel schema migration; Marten, and Polecat 5.20+)
                """;
    }

    private static async Task<List<string>> findUnhealthyResourcesAsync(IServiceProvider services,
        CancellationToken ct)
    {
        var unhealthy = new List<string>();

        foreach (var part in services.GetServices<ISystemPart>())
        {
            var resources = await part.FindResources().ConfigureAwait(false);

            foreach (var resource in resources)
            {
                try
                {
                    await resource.Check(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Check() signals "not configured" by throwing; that is its whole contract.
                    unhealthy.Add($"{resource.Type} '{resource.Name}'");
                }
            }
        }

        return unhealthy;
    }
}
