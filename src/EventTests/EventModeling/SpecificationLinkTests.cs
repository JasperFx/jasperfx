using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling
{
    /// <summary>
    /// jasperfx#995 — specification links without strings: a typed <c>LinksToSpecification</c>, and links
    /// derived from a spec manifest joined by command type.
    /// </summary>
    public class SpecificationLinkTests
    {
        private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

        // ---- the typed overload ------------------------------------------------------------------

        [Fact]
        public void the_typed_overload_derives_the_identity_bobcat_publishes()
        {
            var model = new EventModelBuilder();
            model.Command<ApplyToVolunteer>()
                .LinksToSpecification<apply_to_volunteer>(nameof(apply_to_volunteer.volunteer_application_submitted));

            model.BuildSlices().Single().Specifications.ShouldHaveSingleItem().Identity
                .ShouldBe("apply to volunteer/volunteer application submitted");
        }

        [Fact]
        public void the_string_overload_is_unchanged()
        {
            var model = new EventModelBuilder();
            model.Command<ApplyToVolunteer>().LinksToSpecification("Volunteers/Apply");

            model.BuildSlices().Single().Specifications.ShouldHaveSingleItem().Identity.ShouldBe("Volunteers/Apply");
        }

        // The expected values are Bobcat's own, from MarkerSpecNamingAgreementTests: this is a twin of its
        // rule, and a divergence would silently link to nothing.
        [Theory]
        [InlineData("rebuilding_projections", "rebuilding projections")]
        [InlineData("_2_proposals", "2 proposals")]
        [InlineData("EventsThenResponse", "Events Then Response")]
        [InlineData("HTTPResponse", "HTTP Response")]
        public void identifiers_read_as_sentences_the_way_bobcat_reads_them(string identifier, string expected)
        {
            SpecificationIdentity.Prettify(identifier).ShouldBe(expected);
        }

        [Theory]
        [InlineData("WalletSpecs", "Wallet")]
        [InlineData("WalletSpec", "Wallet")]
        [InlineData("WalletSpecification", "Wallet")]
        [InlineData("WalletFixture", "Wallet")]
        [InlineData("Specs", "Specs")]
        [InlineData("apply_to_volunteer", "apply to volunteer")]
        public void one_suffix_is_removed_from_the_feature_title(string className, string expected)
        {
            SpecificationIdentity.FeatureTitle(className, null).ShouldBe(expected);
        }

        [Fact]
        public void a_bobcat_feature_title_wins_and_is_read_by_attribute_name()
        {
            SpecificationIdentity.For(typeof(TitledSpecs), nameof(TitledSpecs.a_wallet_is_credited))
                .ShouldBe("Wallet credits/a wallet is credited");

            // [BobcatFeature] with no title falls back to the class name, as Bobcat does.
            SpecificationIdentity.FeatureTitle(typeof(UntitledSpecs)).ShouldBe("Untitled");
        }

        [Fact]
        public void a_link_needs_a_scenario()
        {
            Should.Throw<ArgumentException>(() => SpecificationIdentity.For(typeof(apply_to_volunteer), " "));
        }

        // ---- the Specified rung ------------------------------------------------------------------

        [Fact]
        public void specified_ranks_between_declared_and_derived_although_its_number_is_highest()
        {
            ((int)EventModelProvenance.Specified).ShouldBe(3);

            EventModelProvenance.Specified.Outranks(EventModelProvenance.Declared).ShouldBeTrue();
            EventModelProvenance.Derived.Outranks(EventModelProvenance.Specified).ShouldBeTrue();
            EventModelProvenance.Observed.Outranks(EventModelProvenance.Specified).ShouldBeTrue();
        }

        [Fact]
        public void a_derived_claim_beats_a_specified_one_in_a_merge()
        {
            var specified = EventModelSliceDescriptor.Named("S") with
            {
                EmittedEvents = [T<VolunteerApplied>()], Provenance = EventModelProvenance.Specified,
            };
            var derived = EventModelSliceDescriptor.Named("S") with
            {
                EmittedEvents = [T<VolunteerRejected>()], Provenance = EventModelProvenance.Derived,
            };

            specified.Merge(derived).EmittedEvents.ShouldHaveSingleItem().ShouldBe(T<VolunteerRejected>());
            derived.Merge(specified).EmittedEvents.ShouldHaveSingleItem().ShouldBe(T<VolunteerRejected>());
            specified.Merge(derived).Provenance.ShouldBe(EventModelProvenance.Derived);
        }

        // ---- the join ----------------------------------------------------------------------------

        private static EventModelSliceDescriptor commandSlice<TCommand>(string? name = null, string? domain = null,
            TypeDescriptor? handler = null)
            => EventModelSliceDescriptor.Named(name ?? typeof(TCommand).Name) with
            {
                CommandType = T<TCommand>(),
                HandlerType = handler,
                Domain = domain,
                Pattern = SlicePattern.Command,
                Provenance = EventModelProvenance.Derived,
            };

        private static EventModelDescriptor model(params EventModelSliceDescriptor[] slices) => new("App", slices);

        private static SpecificationBindingDescriptor spec<TCommand>(string identity)
            => new(identity) { CommandType = T<TCommand>() };

        [Fact]
        public void a_specification_links_to_the_slice_handling_its_command()
        {
            var linked = EventModelSpecifications.Link(
                model(commandSlice<ApplyToVolunteer>(), commandSlice<RejectVolunteer>()),
                [
                    spec<ApplyToVolunteer>("Volunteers/apply") with { ResolvedTypes = [T<ApplyToVolunteer>(), T<VolunteerApplied>()] },
                    spec<ApplyToVolunteer>("Volunteers/apply twice"),
                ]);

            var apply = linked.Slices[0];
            apply.Specifications.Select(x => x.Identity).ShouldBe(["Volunteers/apply", "Volunteers/apply twice"]);
            apply.Specifications[0].ResolvedTypes.ShouldBe([T<ApplyToVolunteer>(), T<VolunteerApplied>()]);
            apply.ProvenanceFor(EventModelRole.Specifications).ShouldBe(EventModelProvenance.Specified);

            linked.Slices[1].Specifications.ShouldBeEmpty();
            linked.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_specification_whose_command_no_slice_handles_is_not_linked()
        {
            var linked = EventModelSpecifications.Link(model(commandSlice<RejectVolunteer>()),
                [spec<ApplyToVolunteer>("Volunteers/apply")]);

            linked.Slices.Single().Specifications.ShouldBeEmpty();
            linked.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_command_several_slices_handle_is_a_hotspot_rather_than_a_guess()
        {
            var linked = EventModelSpecifications.Link(
                model(commandSlice<ApplyToVolunteer>("Shelter.ApplyToVolunteer", "Shelter"),
                    commandSlice<ApplyToVolunteer>("Clinic.ApplyToVolunteer", "Clinic")),
                [spec<ApplyToVolunteer>("Volunteers/apply")]);

            linked.Slices.ShouldAllBe(x => x.Specifications.Count == 0);

            var hotspot = linked.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.UnresolvedSpecification);
            hotspot.SpecificationIdentity.ShouldBe("Volunteers/apply");
            hotspot.Text.ShouldContain("Shelter.ApplyToVolunteer");
            hotspot.Text.ShouldContain("Clinic.ApplyToVolunteer");
        }

        [Fact]
        public void a_domain_chooses_between_slices_handling_the_same_command()
        {
            var linked = EventModelSpecifications.Link(
                model(commandSlice<ApplyToVolunteer>("Shelter.ApplyToVolunteer", "Shelter"),
                    commandSlice<ApplyToVolunteer>("Clinic.ApplyToVolunteer", "Clinic")),
                [spec<ApplyToVolunteer>("Volunteers/apply") with { Domain = "Clinic" }]);

            linked.Slices.Single(x => x.Domain == "Clinic").Specifications.ShouldHaveSingleItem();
            linked.Slices.Single(x => x.Domain == "Shelter").Specifications.ShouldBeEmpty();
            linked.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_namespace_chooses_between_slices_handling_the_same_command()
        {
            var linked = EventModelSpecifications.Link(
                model(commandSlice<ApplyToVolunteer>("A", handler: T<SpecLinkModules.Shelter.ApplyHandler>()),
                    commandSlice<ApplyToVolunteer>("B", handler: T<SpecLinkModules.Clinic.ApplyHandler>())),
                [spec<ApplyToVolunteer>("Volunteers/apply") with { Namespace = "SpecLinkModules.Clinic" }]);

            linked.Slices.Single(x => x.Name == "B").Specifications.ShouldHaveSingleItem();
            linked.Slices.Single(x => x.Name == "A").Specifications.ShouldBeEmpty();
        }

        [Fact]
        public void an_explicit_slice_overrides_the_inference()
        {
            var linked = EventModelSpecifications.Link(
                model(commandSlice<ApplyToVolunteer>(), commandSlice<RejectVolunteer>()),
                [spec<ApplyToVolunteer>("Volunteers/reject after applying") with { SliceName = nameof(RejectVolunteer) }]);

            linked.Slices.Single(x => x.Name == nameof(RejectVolunteer)).Specifications.ShouldHaveSingleItem();
            linked.Slices.Single(x => x.Name == nameof(ApplyToVolunteer)).Specifications.ShouldBeEmpty();
        }

        [Fact]
        public void an_explicit_slice_the_model_does_not_have_is_reported()
        {
            var linked = EventModelSpecifications.Link(model(commandSlice<ApplyToVolunteer>()),
                [new SpecificationBindingDescriptor("Volunteers/x") { SliceName = "Nowhere" }]);

            linked.Hotspots.ShouldHaveSingleItem().Text.ShouldContain("Nowhere");
        }

        [Fact]
        public void a_declared_link_that_agrees_records_nothing()
        {
            var declared = commandSlice<ApplyToVolunteer>() with
            {
                Specifications = [new SpecificationDescriptor("Volunteers/apply")],
                Provenance = EventModelProvenance.Declared,
            };

            var linked = EventModelSpecifications.Link(model(declared), [spec<ApplyToVolunteer>("Volunteers/apply")]);

            var slice = linked.Slices.Single();
            slice.Specifications.ShouldHaveSingleItem().Identity.ShouldBe("Volunteers/apply");
            slice.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_declared_link_that_disagrees_loses_and_is_recorded()
        {
            var declared = commandSlice<ApplyToVolunteer>() with
            {
                Specifications = [new SpecificationDescriptor("Volunteers/an old name")],
                Provenance = EventModelProvenance.Declared,
            };

            var linked = EventModelSpecifications.Link(model(declared), [spec<ApplyToVolunteer>("Volunteers/apply")]);

            var slice = linked.Slices.Single();
            slice.Specifications.ShouldHaveSingleItem().Identity.ShouldBe("Volunteers/apply");

            var hotspot = slice.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
            hotspot.Role.ShouldBe(EventModelRole.Specifications);
            hotspot.WinningClaim!.Provenance.ShouldBe(EventModelProvenance.Specified);
            hotspot.LosingClaim!.Value.ShouldBe("Volunteers/an old name");
        }

        [Fact]
        public void the_same_unresolved_specification_is_one_hotspot()
        {
            var ambiguous = model(commandSlice<ApplyToVolunteer>("A", "X"), commandSlice<ApplyToVolunteer>("B", "Y"));

            var once = EventModelSpecifications.Link(ambiguous, [spec<ApplyToVolunteer>("Volunteers/apply")]);
            var twice = EventModelSpecifications.Link(once, [spec<ApplyToVolunteer>("Volunteers/apply")]);

            twice.Hotspots.ShouldHaveSingleItem();
        }

        public record ApplyToVolunteer;
        public record RejectVolunteer;
        public record VolunteerApplied;
        public record VolunteerRejected;

        public class apply_to_volunteer
        {
            public void volunteer_application_submitted() { }
        }

        [BobcatFeature("Wallet credits")]
        public class TitledSpecs
        {
            public void a_wallet_is_credited() { }
        }

        [BobcatFeature]
        public class UntitledSpecs;
    }

    /// <summary>Stands in for Bobcat's attribute: matched by name, never referenced.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BobcatFeatureAttribute(string? title = null) : Attribute
    {
        public string? Title { get; } = title;
    }
}

namespace SpecLinkModules.Shelter
{
    public class ApplyHandler;
}

namespace SpecLinkModules.Clinic
{
    public class ApplyHandler;
}
