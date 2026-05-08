using System.Text.Json;

namespace JasperFx.Descriptors;

/// <summary>
/// Result of rehydrating a DCB-projected entity by tag set: the projected
/// state at a given version expressed as raw JSON. Returned by the event
/// store explorer when an operator queries by tag values rather than by
/// stream id.
/// </summary>
/// <param name="ProjectionName">Name of the projection that produced <paramref name="State"/>.</param>
/// <param name="Version">Logical version of the projected state (typically the event sequence floor used during rehydration).</param>
/// <param name="State">Projected state expressed as JSON.</param>
/// <param name="EventsApplied">Number of events that were folded into <paramref name="State"/>.</param>
public sealed record DcbProjectedState(
    string ProjectionName,
    long Version,
    JsonElement State,
    int EventsApplied);
