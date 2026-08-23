using JasperFx.Events;

namespace DocSamples.EventModeling;

// The IncidentService sample domain from Wolverine:
// https://github.com/JasperFx/wolverine/tree/main/src/Samples/IncidentService/IncidentService
//
// Trimmed to what the Event Modeling docs need, and with the Wolverine / Marten attributes
// left off so this project keeps compiling without those references. The shapes — the events,
// the aggregate, the commands, the endpoint types — are the sample's own.

#region sample_incident_service_events

public record IncidentLogged(Guid CustomerId, Contact Contact, string Description, Guid LoggedBy);

public record IncidentCategorised(Guid IncidentId, IncidentCategory Category, Guid CategorisedBy);

public record IncidentClosed(Guid ClosedBy);

#endregion

#region sample_incident_service_aggregate

public class Incident
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    public IncidentCategory? Category { get; set; }
    public bool HasOutstandingResponseToCustomer { get; set; }

    public void Apply(IncidentLogged _) { }
    public void Apply(IncidentCategorised e) => Category = e.Category;
    public void Apply(IncidentClosed _) => Status = IncidentStatus.Closed;

    public bool ShouldDelete(Archived @event) => true;
}

#endregion

#region sample_incident_service_commands

public record LogIncident(Guid CustomerId, Contact Contact, string Description, Guid LoggedBy);

public record CategoriseIncident(IncidentCategory Category, Guid CategorisedBy, int Version);

public record CloseIncident(Guid ClosedBy, int Version);

public record ArchiveIncident(Guid IncidentId);

#endregion

// The Wolverine HTTP endpoints and message handler. Stand-ins here: in the real sample these
// carry [WolverinePost] / [Aggregate] and it is those attributes a derived source reads.
public static class LogIncidentEndpoint;

public static class CategoriseIncidentEndpoint;

public static class CloseIncidentEndpoint;

public static class GetIncidentEndpoint;

public static class ArchiveIncidentHandler;

public record IncidentDetails(Guid Id, IncidentStatus Status, IncidentCategory? Category);

public record CustomerIncidentsSummary(Guid CustomerId, int Pending, int Closed);

public record Contact(ContactChannel ContactChannel, string? EmailAddress = null);

public enum ContactChannel
{
    Email,
    Phone,
    InPerson,
    GeneratedBySystem
}

public enum IncidentStatus
{
    Pending = 1,
    Resolved = 8,
    ResolutionAcknowledgedByCustomer = 16,
    Closed = 32
}

public enum IncidentCategory
{
    Software,
    Hardware,
    Network,
    Database
}
