using System.Diagnostics.Metrics;
using JasperFx.Descriptors;

namespace JasperFx.OpenTelemetry;

/// <summary>
/// Common OpenTelemetry configuration shared by Critter Stack tools: the connection/session
/// tracking level and the per-tool <see cref="System.Diagnostics.Metrics.Meter"/> used for
/// custom metrics.
/// </summary>
/// <remarks>
/// Lifted as the common base from Marten's and Polecat's near-identical
/// <c>OpenTelemetryOptions</c>. The only structural difference was the meter name
/// ("Marten" vs "Polecat"), supplied here via the ctor. Marten's changeset-metrics surface
/// (<c>ExportCounterOnChangeSets</c> / <c>TrackEventCounters</c>, which depend on Marten's
/// <c>IChangeSet</c>) stays in a Marten-side derived class. Part of the Critter Stack 2026
/// dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
///
/// The pass-N audit deliberately left the <c>MartenTracing</c> / <c>PolecatTracing</c>
/// ActivitySource holders per-library (they're tiny, the source name is baked in, and OTel
/// filtering wants per-library sources) — only this options type + <see cref="TrackLevel"/>
/// are lifted.
/// </remarks>
public class OpenTelemetryOptions
{
    /// <param name="meterName">
    /// The <see cref="System.Diagnostics.Metrics.Meter"/> name for this tool's custom metrics
    /// (e.g. "Marten", "Polecat").
    /// </param>
    public OpenTelemetryOptions(string meterName)
    {
        Meter = new Meter(meterName);
    }

    /// <summary>
    /// Used to track OpenTelemetry events for opening a connection or exceptions on a
    /// connection (e.g. when a command or data reader is executed). Defaults to
    /// <see cref="TrackLevel.None"/>.
    /// </summary>
    public TrackLevel TrackConnections { get; set; } = TrackLevel.None;

    /// <summary>
    /// The meter used to create custom counters/instruments for this tool.
    /// </summary>
    [IgnoreDescription]
    public Meter Meter { get; }
}
