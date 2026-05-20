namespace JasperFx.OpenTelemetry;

/// <summary>
/// Controls the level of OpenTelemetry tracking emitted by a Critter Stack tool.
/// </summary>
/// <remarks>
/// Lifted from the near-identical <c>TrackLevel</c> enums in Marten (<c>Marten.Services</c>)
/// and Polecat (<c>Polecat.Internal.OpenTelemetry</c>). Part of the Critter Stack 2026 dedupe
/// pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>). Each store
/// type-forwards its old public name to this type.
/// </remarks>
public enum TrackLevel
{
    /// <summary>
    /// No OpenTelemetry tracking.
    /// </summary>
    None,

    /// <summary>
    /// Normal level of OpenTelemetry tracking (connection / session lifecycle).
    /// </summary>
    Normal,

    /// <summary>
    /// Very verbose tracking, only suitable for debugging or performance tuning.
    /// </summary>
    Verbose
}
