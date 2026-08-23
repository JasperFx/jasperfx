namespace JasperFx.Events.EventModeling;

/// <summary>
/// Which way messages flow between the model and an external system.
/// </summary>
public enum ExternalSystemDirection
{
    /// <summary>The external system pushes into the model — it is the slice's trigger (translation in).</summary>
    Inbound,

    /// <summary>The model pushes out to the external system — it receives the slice's published messages (translation out).</summary>
    Outbound,
}

/// <summary>
/// An external system on one end of an integration edge (jasperfx#687 decision 5).
/// The <em>edge</em> is derived from the Wolverine endpoint the messages travel
/// over; only the <see cref="Name"/> is ever declared, and it is declared on that
/// endpoint's configuration (wolverine#3989), not through the overlay here.
/// </summary>
/// <param name="Name">Display name of the external system (e.g. "Stripe", "Legacy ERP").</param>
/// <param name="Direction">Whether the system feeds the slice or is fed by it.</param>
/// <param name="EndpointUri">The Wolverine endpoint URI the edge was derived from, when known.</param>
public sealed record ExternalSystemDescriptor(
    string Name,
    ExternalSystemDirection Direction,
    string? EndpointUri = null);
