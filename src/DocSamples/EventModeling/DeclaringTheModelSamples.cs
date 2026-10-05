using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace DocSamples.EventModeling;

// jasperfx#961: the samples for "Declaring the Model in Code". A feature IncidentService has not built
// yet -- escalating an incident to the on-call agent -- designed from stub types first.

#region sample_escalation_stub_types

// Stubs first. They carry nothing yet, and that's fine -- they're real types the model, the specs
// and eventually the handlers can all refer to.
public record EscalateIncident;
public record IncidentEscalated;
public record SendEscalationDigest;
public record EscalationQueue(Guid Id);

#endregion

#region sample_declaring_the_escalation_feature

public class EscalationModel : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.InDomain("Incidents").InChapter("Escalation");

        // LogIncident already exists; say that it starts the Incident stream
        model.Command<LogIncident>()
            .TriggeredBy("Customer submits the incident form", TriggerKind.Http)
            .StartsStream<Incident>()
            .Emits<IncidentLogged>();

        model.Command<EscalateIncident>()
            .TriggeredBy("Agent clicks Escalate", TriggerKind.Http)
            .Against<Incident>()
            .Emits<IncidentEscalated>();

        // The system reacting to an escalation. Nobody has written a PageOnCallAgent
        // type yet, so name it for now and stub it later
        model.Automation("PageOnCallAgent")
            .On<IncidentEscalated>()
            .Publishes("PageOnCallAgent");

        // A command fired by Wolverine's scheduled (cron) messages
        model.Automation<SendEscalationDigest>()
            .Reads<EscalationQueue>();

        model.View<EscalationQueue>()
            .From<IncidentEscalated>()
            .From<IncidentClosed>();
    }
}

#endregion

#region sample_declaring_roles_by_name

public class NotStubbedYet : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Command("ReassignIncident")
            .Against("Incident")
            .Emits("IncidentReassigned")
            .Hotspot("Does a reassignment reset the response SLA?");
    }
}

#endregion

#region sample_declaring_aggregates

public class HelpdeskAggregates : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        model.Aggregate<Incident>();
        model.Aggregate("Customer", AggregateKind.ReadAggregate);
    }
}

#endregion

#region sample_declaring_modules_as_domains

public class HelpdeskModules : EventModelDefinition
{
    public override string Name => "Helpdesk";

    public override void Configure(EventModelBuilder model)
    {
        // Everything in a namespace, and the namespaces beneath it...
        model.Domain("Incidents").IncludesNamespace("IncidentService.Incidents");

        // ...or the namespace of a marker type...
        model.Domain("Billing").IncludesNamespaceOf<BillingModule>();

        // ...or one specific handler
        model.Domain("Retention").Includes<IncidentRetentionHandler>();
    }
}

#endregion

public class BillingModule;

#region sample_domain_attribute_on_a_handler

[Domain("Retention")]
public class IncidentRetentionHandler
{
    public void Handle(ArchiveIncident command)
    {
        // archive the incident
    }
}

#endregion

public static class DeclaringTheModelUsage
{
    public static void Register(IServiceCollection services)
    {
        #region sample_registering_declared_models

        services.AddEventModel<EscalationModel>();
        services.AddEventModel<HelpdeskModules>();

        #endregion
    }
}
