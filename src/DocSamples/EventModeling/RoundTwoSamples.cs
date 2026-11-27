using JasperFx;
using JasperFx.Events.EventModeling;

namespace DocSamples.EventModeling;

// jasperfx#1297, #1298, #1299: the samples for the second Event Modeling wave -- consistency boundaries,
// the structural elements, and field shapes -- on a feature IncidentService has not built yet: assigning an
// incident to an agent who has room for it.

#region sample_dcb_stub_types

// A strong-typed id is a consistency tag: events carry it, and a boundary query selects by it
public record AgentId(Guid Value);

public record AssignIncident(IncidentId IncidentId, AgentId AgentId);
public record IncidentAssigned(IncidentId IncidentId, AgentId AgentId);

// The decider model: how many incidents an agent holds, folded over the events tagged with their id
public class AgentWorkload;

// A read model consulted before deciding, and one the boundary deliberately leaves out
public class AgentRoster;
public class AssignmentReport;

#endregion

#region sample_declaring_consistency_boundaries

public class IncidentAssignment : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // The tag catalogue. AgentId is the tag "agentId", a Guid inside; IncidentId is "incidentId"
        model.Tag<AgentId, Guid>();
        model.Tag<IncidentId>();

        model.Command<AssignIncident>()
            // What the command reads, and by which tag. The boundary is derived from these
            .Decides<AgentWorkload>().ByTag<AssignIncident, AgentId>(x => x.AgentId)
            .Reads<AgentRoster>().By<AssignIncident, AgentId>(x => x.AgentId)
            // What it appends, and the tags the event carries
            .Emits<IncidentAssigned>().TaggedBy<AgentId>().TaggedBy<IncidentId>()
            // A projection fed by the event that no decision depends on
            .UntaggedProjection<AssignmentReport>()
            // The ways it can refuse
            .Refuses("Agent is at capacity")
            .Refuses("Incident is already assigned");
    }
}

#endregion

#region sample_structural_stub_types

public record PaymentCaptured;
public record PaymentRecorded;
public record SendShiftDigest;
public class ShiftRoster;

#endregion

#region sample_declaring_structural_elements

public class HelpdeskStructure : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command<AssignIncident>()
            // Who triggers the slice, and what they act through
            .TriggeredBy("Coordinator assigns the incident", TriggerKind.Human)
            .Actor("Coordinator", "dispatch")
            .FromScreen("Assignment board")
            // Prose, for the slice and for one of its elements
            .Describe("A coordinator hands an open incident to an agent with room for it.")
            .Describe<IncidentAssigned>("The incident now counts against the agent's capacity.");

        // An event from outside the model, translated in
        model.Translation("RecordPayment")
            .From<PaymentCaptured>().External("Payments")
            .Emits<PaymentRecorded>();

        // A scheduled automation, with its cadence
        model.Automation<SendShiftDigest>()
            .Every(TimeSpan.FromHours(8))
            .Reads<ShiftRoster>();
    }
}

#endregion

#region sample_field_attributes

public record IncidentEscalatedToAgent(
    // The system mints it, so no command has to supply it
    [property: Generated] Guid EscalationId,
    IncidentId IncidentId,
    // Personally identifiable; a canvas and an export mark it
    [property: Pii] string AgentEmail,
    DateTimeOffset? Deadline);

#endregion

#region sample_declaring_field_shapes

public class HelpdeskShapes : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command<AssignIncident>()
            .Against<Incident>()
            .Emits<IncidentAssigned>();

        model.View<AgentRoster>().From<IncidentAssigned>();

        // The fields the stubs will have, declared before they do. Once the types exist, the
        // source generator reads the real fields and these become the to-do list
        model.Shape<AssignIncident>()
            .Field("IncidentId").OfType<IncidentId>().Identity()
            .Field("AgentId").OfType<AgentId>().Tagged<AgentId>();

        model.Shape<IncidentAssigned>()
            .Field("IncidentId").OfType<IncidentId>().Identity()
            .Field("AgentId").OfType<AgentId>().Tagged<AgentId>()
            .Field("AssignedAt").OfType<DateTimeOffset>().Generated();

        model.Shape<AgentRoster>()
            .Field("Id").OfType<AgentId>().Identity()
            // Named differently from the event's field, so say where it comes from
            .Field("Incidents").OfType<IncidentId>().Many().From<IncidentAssigned>(x => x.IncidentId)
            .Field("Notes").Optional().Describe("Free text the coordinator keeps per agent");
    }
}

#endregion
