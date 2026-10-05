using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling
{
    using Modules.Billing;
    using Modules.Billing.Invoices;
    using Modules.BillingReports;
    using Modules.Shipping;

    /// <summary>
    /// jasperfx#960: in a modular monolith a module is a Domain, and membership is declared — by policy
    /// or by attribute — never inferred.
    /// </summary>
    public class DomainDeclarationTests
    {
        #region sample_declaring_modules_as_domains

        public class ModularMonolithModel : EventModelDefinition
        {
            public override void Configure(EventModelBuilder model)
            {
                // Everything in the Billing module's namespace is Billing...
                model.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");

                // ...and Shipping is declared by a marker type's namespace
                model.Domain("Shipping").IncludesNamespaceOf<ShippingModule>();
            }
        }

        #endregion

        private static IReadOnlyList<DomainAssignmentDescriptor> policies(Action<EventModelBuilder> configure)
        {
            var model = new EventModelBuilder();
            configure(model);
            return model.Build("app").DomainAssignments;
        }

        private static IReadOnlyList<DomainAssignmentDescriptor> modular()
        {
            var model = new EventModelBuilder();
            new ModularMonolithModel().Configure(model);
            return model.Build("app").DomainAssignments;
        }

        [Fact]
        public void the_same_message_handled_in_two_modules_resolves_to_two_domains()
        {
            // The shape of JasperFx/wolverine#4829: one message, one handler per module.
            EventModelDomains.Resolve(typeof(Modules.Billing.OrderPlacedHandler), null, modular())
                .ShouldBe(new DomainResolution("Billing", DomainResolutionSource.NamespacePolicy, []), new ResolutionComparer());
            EventModelDomains.Resolve(typeof(Modules.Shipping.OrderPlacedHandler), null, modular())
                .Domain.ShouldBe("Shipping");
        }

        [Fact]
        public void a_namespace_policy_covers_the_namespaces_beneath_it_but_not_lookalikes()
        {
            EventModelDomains.Resolve(typeof(InvoiceHandler), null, modular()).Domain.ShouldBe("Billing");

            // "Modules.BillingReports" is not inside "Modules.Billing"
            EventModelDomains.Resolve(typeof(ReportHandler), null, modular())
                .ShouldBe(DomainResolution.Undeclared);
        }

        [Fact]
        public void the_deepest_namespace_wins()
        {
            var resolution = EventModelDomains.Resolve(typeof(InvoiceHandler), null, policies(m =>
            {
                m.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");
                m.Domain("Invoicing").IncludesNamespace("EventTests.EventModeling.Modules.Billing.Invoices");
            }));

            resolution.Domain.ShouldBe("Invoicing");
            resolution.Conflicts.ShouldBe(["Billing"]);
        }

        [Fact]
        public void a_namespace_beats_an_assembly()
        {
            var resolution = EventModelDomains.Resolve(typeof(Modules.Billing.OrderPlacedHandler), null, policies(m =>
            {
                m.Domain("Everything").Includes(typeof(DomainDeclarationTests).Assembly);
                m.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");
            }));

            resolution.Domain.ShouldBe("Billing");
            resolution.Source.ShouldBe(DomainResolutionSource.NamespacePolicy);
            resolution.Conflicts.ShouldBe(["Everything"]);
        }

        [Fact]
        public void an_assembly_policy_applies_when_nothing_narrower_does()
        {
            var resolution = EventModelDomains.Resolve(typeof(ReportHandler), null,
                policies(m => m.Domain("Everything").Includes(typeof(DomainDeclarationTests).Assembly)));

            resolution.Domain.ShouldBe("Everything");
            resolution.Source.ShouldBe(DomainResolutionSource.AssemblyPolicy);
        }

        [Fact]
        public void a_type_policy_beats_its_namespace()
        {
            var resolution = EventModelDomains.Resolve(typeof(InvoiceHandler), null, policies(m =>
            {
                m.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");
                m.Domain("Ledger").Includes<InvoiceHandler>();
            }));

            resolution.Domain.ShouldBe("Ledger");
            resolution.Source.ShouldBe(DomainResolutionSource.TypePolicy);
        }

        [Fact]
        public void an_attribute_beats_every_policy_and_the_policy_it_overrode_is_reported()
        {
            // AuditedHandler sits in the Billing namespace but declares itself Shipping
            var resolution = EventModelDomains.Resolve(typeof(AuditedHandler), null, modular());

            resolution.Domain.ShouldBe("Shipping");
            resolution.Source.ShouldBe(DomainResolutionSource.TypeAttribute);

            // The conflict is the point: someone should be asked which is right (ai-skills follow-up)
            resolution.Conflicts.ShouldBe(["Billing"]);
        }

        [Fact]
        public void a_method_attribute_beats_its_class()
        {
            var method = typeof(AuditedHandler).GetMethod(nameof(AuditedHandler.HandleRefund))!;

            var resolution = EventModelDomains.Resolve(typeof(AuditedHandler), method, modular());

            resolution.Domain.ShouldBe("Refunds");
            resolution.Source.ShouldBe(DomainResolutionSource.MethodAttribute);
            resolution.Conflicts.ShouldBe(["Shipping", "Billing"]);
        }

        [Fact]
        public void two_policies_naming_different_domains_at_one_level_are_ambiguous_never_guessed()
        {
            var resolution = EventModelDomains.Resolve(typeof(Modules.Billing.OrderPlacedHandler), null, policies(m =>
            {
                m.Domain("Billing").IncludesNamespace("EventTests.EventModeling.Modules.Billing");
                m.Domain("Finance").IncludesNamespace("EventTests.EventModeling.Modules.Billing");
            }));

            resolution.Domain.ShouldBeNull();
            resolution.IsAmbiguous.ShouldBeTrue();
            resolution.Source.ShouldBe(DomainResolutionSource.NamespacePolicy);
            resolution.Conflicts.ShouldBe(["Billing", "Finance"]);
        }

        [Fact]
        public void nothing_declared_is_undeclared()
        {
            var resolution = EventModelDomains.Resolve(typeof(ReportHandler), null, []);

            resolution.ShouldBe(DomainResolution.Undeclared);
            resolution.IsAmbiguous.ShouldBeFalse();
        }

        [Fact]
        public void policies_travel_on_the_model_and_union_when_models_merge()
        {
            var billing = new EventModelBuilder();
            billing.Domain("Billing").IncludesNamespace("A").IncludesNamespace("A");
            var shipping = new EventModelBuilder();
            shipping.Domain("Shipping").IncludesNamespace("B");
            shipping.Domain("Billing").IncludesNamespace("A");

            var merged = EventModelDescriptor.Merge("app", [billing.Build("app"), shipping.Build("app")]);

            merged.DomainAssignments.ShouldBe([
                new DomainAssignmentDescriptor("Billing", DomainAssignmentScope.Namespace, "A"),
                new DomainAssignmentDescriptor("Shipping", DomainAssignmentScope.Namespace, "B"),
            ]);
        }

        [Fact]
        public void a_domain_needs_a_name()
        {
            Should.Throw<ArgumentException>(() => new EventModelBuilder().Domain(" "));
            Should.Throw<ArgumentException>(() => new DomainAttribute(""));
        }

        [Fact]
        public void the_scope_integers_are_stable_on_the_wire()
        {
            ((int)DomainAssignmentScope.Assembly).ShouldBe(0);
            ((int)DomainAssignmentScope.Namespace).ShouldBe(1);
            ((int)DomainAssignmentScope.Type).ShouldBe(2);
        }

        private sealed class ResolutionComparer : IEqualityComparer<DomainResolution>
        {
            public bool Equals(DomainResolution? x, DomainResolution? y)
                => x!.Domain == y!.Domain && x.Source == y.Source && x.Conflicts.SequenceEqual(y.Conflicts);

            public int GetHashCode(DomainResolution obj) => 0;
        }
    }
}

namespace EventTests.EventModeling.Modules.Billing
{
    public record OrderPlaced(Guid OrderId);

    public record InvoiceRequested(Guid OrderId);

    public class OrderPlacedHandler
    {
        public InvoiceRequested Handle(OrderPlaced e) => new(e.OrderId);
    }

    [Domain("Shipping")]
    public class AuditedHandler
    {
        public void Handle(OrderPlaced e) { }

        [Domain("Refunds")]
        public void HandleRefund(OrderPlaced e) { }
    }
}

namespace EventTests.EventModeling.Modules.Billing.Invoices
{
    public class InvoiceHandler;
}

namespace EventTests.EventModeling.Modules.BillingReports
{
    public class ReportHandler;
}

namespace EventTests.EventModeling.Modules.Shipping
{
    public class ShippingModule;

    public class OrderPlacedHandler
    {
        public void Handle(Billing.OrderPlaced e) { }
    }
}
