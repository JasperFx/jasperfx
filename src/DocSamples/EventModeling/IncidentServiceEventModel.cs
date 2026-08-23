using JasperFx.Events.EventModeling;

namespace DocSamples.EventModeling;

#region sample_incident_service_overlay

public class IncidentServiceModel : EventModelDefinition
{
    // Every source that contributes to "Helpdesk" is folded into one model under this name
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.InDomain("Incidents");

        builder.Slice("LogIncident")
            .TriggeredBy("Customer submits the incident form")
            .LinksToSpecification("Log Incident/Logs an incident from the web form");

        builder.Slice("CategoriseIncident")
            .TriggeredBy("Agent picks a category");

        builder.Slice("CloseIncident")
            .TriggeredBy("Agent clicks Close");

        builder.Slice("ArchiveIncident")
            .InDomain("Retention")
            .TriggeredBy("Three days after close");

        builder.Slice("GetIncident")
            .TriggeredBy("Agent opens the incident detail screen");
    }
}

#endregion

#region sample_incident_service_hotspots

public class IncidentServiceHotspots : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        // Not about any one slice — nobody has decided this yet
        builder.Hotspot("Do we own the SLA clock, or does the CRM?");

        builder.Slice("CloseIncident")
            // Both of these rules are sitting commented out in CloseIncidentEndpoint.
            // Written down here, they show up on the canvas instead of in a code comment
            // nobody outside the team will ever read.
            .Hotspot("Can an incident be closed before the customer acknowledges the resolution?")
            .Hotspot("What happens to an outstanding response to the customer when we close?");

        builder.Slice("ArchiveIncident")
            .Hotspot("Three days is a guess. Ask legal what retention actually requires.");
    }
}

#endregion

#region sample_incident_service_pending_spec_hotspot

public class ResolutionSlices : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.Slice("ResolveIncident")
            // The question is sharp enough to name a scenario, so name it. The spec is
            // pending, and a pending spec is a hotspot — one that retires itself the day
            // the spec passes, which a prose note never does.
            .TriggeredBy("Agent clicks Resolve")
            .LinksToSpecification("Resolve Incident/An agent resolves a pending incident");
    }
}

#endregion

#region sample_incident_service_external_flow

public class CrmNotification : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder builder)
    {
        builder.Slice("NotifyCrmOfClosure")
            .InDomain("Integrations")
            .TriggeredBy("Incident closed")
            .ForFlowNotOwnedHere(roles => roles
                .Pattern(SlicePattern.Translation)
                .TriggeredBy(TriggerKind.MessageHandler)
                .UsesAggregate<Incident>()
                .ExternalSystem("Salesforce", ExternalSystemDirection.Outbound, "rabbitmq://queue/crm-updates"));
    }
}

#endregion
