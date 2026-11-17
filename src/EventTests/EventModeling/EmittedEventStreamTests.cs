using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling.StreamLanes;

/// <summary>
/// jasperfx#1203: an emitted event can name the stream it goes to with OnStream().
/// </summary>
public class EmittedEventStreamTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static EventModelSliceDescriptor slice(EventModelBuilder model, string name)
        => model.BuildSlices().Single(x => x.Name == name);

    #region sample_emitted_event_stream_lanes

    public class LeadConversion : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            // Decides against the Lead, starts the Account stream, and
            // adds a Contact to an existing stream
            model.Command<ConvertLeadToAccount>()
                .Against<Lead>()
                .StartsStream<Account>()
                .Against<Contact>()
                .Emits<LeadConvertedToAccount>().OnStream<Lead>()
                .Emits<AccountCreated>().OnStream<Account>()
                .Emits<ContactCreated>().OnStream<Contact>();
        }
    }

    #endregion

    [Fact]
    public void each_emitted_event_records_its_stream()
    {
        var model = new EventModelBuilder();
        new LeadConversion().Configure(model);

        var convert = slice(model, nameof(ConvertLeadToAccount));

        convert.EmittedEvents.ShouldBe([T<LeadConvertedToAccount>(), T<AccountCreated>(), T<ContactCreated>()]);
        convert.EmittedEventStreams.ShouldBe([
            new EmittedEventStream(T<LeadConvertedToAccount>(), T<Lead>()),
            new EmittedEventStream(T<AccountCreated>(), T<Account>()),
            new EmittedEventStream(T<ContactCreated>(), T<Contact>())
        ]);

        convert.StreamFor(T<AccountCreated>()).ShouldBe(T<Account>());
        convert.StreamFor(T<LeadConvertedToAccount>()).ShouldBe(T<Lead>());
    }

    [Fact]
    public void an_emit_without_a_lane_behaves_as_before()
    {
        var model = new EventModelBuilder();
        model.Command<ConvertLeadToAccount>()
            .Against<Lead>()
            .Emits<LeadConvertedToAccount>()
            .Emits<AccountCreated>().OnStream<Lead>();

        var convert = slice(model, nameof(ConvertLeadToAccount));

        convert.EmittedEvents.ShouldBe([T<LeadConvertedToAccount>(), T<AccountCreated>()]);
        convert.StreamFor(T<LeadConvertedToAccount>()).ShouldBeNull();
        convert.EmittedEventStreams.ShouldHaveSingleItem().Event.ShouldBe(T<AccountCreated>());
    }

    [Fact]
    public void on_after_emits_is_still_a_consumed_event()
    {
        // OnStream() is the lane; On() keeps meaning an event the slice reacts to
        var model = new EventModelBuilder();
        model.Automation("ConvertQualifiedLeads")
            .Against<Lead>()
            .Emits<LeadConvertedToAccount>()
            .On<LeadQualified>();

        var automation = slice(model, "ConvertQualifiedLeads");

        automation.ConsumedEvents.ShouldBe([T<LeadQualified>()]);
        automation.EmittedEventStreams.ShouldBeEmpty();
    }

    [Fact]
    public void the_lane_can_be_declared_before_the_aggregate()
    {
        var model = new EventModelBuilder();
        model.Command<ConvertLeadToAccount>()
            .Emits<AccountCreated>().OnStream<Account>()
            .StartsStream<Account>();

        slice(model, nameof(ConvertLeadToAccount)).StreamFor(T<AccountCreated>()).ShouldBe(T<Account>());
    }

    [Fact]
    public void the_lane_can_name_the_default_aggregate()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Lead>();
        model.Command<ConvertLeadToAccount>()
            .Emits<LeadConvertedToAccount>().OnStream<Lead>();

        slice(model, nameof(ConvertLeadToAccount)).StreamFor(T<LeadConvertedToAccount>()).ShouldBe(T<Lead>());
    }

    [Fact]
    public void lanes_work_by_name_before_the_types_exist()
    {
        var model = new EventModelBuilder();
        model.Command("ConvertLead")
            .Against("Lead")
            .StartsStream("Account")
            .Emits("AccountCreated").OnStream("Account");

        var convert = slice(model, "ConvertLead");

        convert.StreamFor(EventModelSliceBuilder.Declared("AccountCreated"))!.Name.ShouldBe("Account");

        // A declared name matches the real type by name
        convert.StreamFor(T<AccountCreated>())!.Name.ShouldBe("Account");
    }

    [Fact]
    public void a_lane_must_name_an_aggregate_the_slice_decides_against_or_starts()
    {
        var model = new EventModelBuilder();
        model.Command<ConvertLeadToAccount>()
            .Against<Lead>()
            .Emits<ContactCreated>().OnStream<Contact>();

        var ex = Should.Throw<InvalidOperationException>(() => model.BuildSlices());
        ex.Message.ShouldContain("ContactCreated");
        ex.Message.ShouldContain("Against<Contact>()");
    }

    [Fact]
    public void a_lane_needs_an_emitted_event_first()
    {
        var model = new EventModelBuilder();

        Should.Throw<InvalidOperationException>(() =>
            model.Command<ConvertLeadToAccount>().Against<Lead>().OnStream<Lead>());
    }

    [Fact]
    public void an_event_cannot_go_to_two_streams()
    {
        var model = new EventModelBuilder();
        var convert = model.Command<ConvertLeadToAccount>()
            .Against<Lead>()
            .StartsStream<Account>()
            .Emits<AccountCreated>().OnStream<Account>();

        // Saying the same thing twice is harmless
        convert.Emits<AccountCreated>().OnStream<Account>();

        Should.Throw<InvalidOperationException>(() => convert.Emits<AccountCreated>().OnStream<Lead>());
    }

    [Fact]
    public void a_declared_lane_survives_the_merge_with_the_code()
    {
        var model = new EventModelBuilder();
        new LeadConversion().Configure(model);
        var declared = slice(model, nameof(ConvertLeadToAccount)).WithProvenance(EventModelProvenance.Declared);

        // The code knows what it emits, never which lane the board put it in
        var derived = EventModelSliceDescriptor.Named(nameof(ConvertLeadToAccount)) with
        {
            EmittedEvents = [T<LeadConvertedToAccount>(), T<AccountCreated>()],
            AggregateTypes = [T<Lead>(), T<Account>()],
            Provenance = EventModelProvenance.Derived,
        };

        foreach (var merged in new[] { declared.Merge(derived), derived.Merge(declared) })
        {
            merged.EmittedEvents.ShouldBe([T<LeadConvertedToAccount>(), T<AccountCreated>()]);
            merged.StreamFor(T<LeadConvertedToAccount>()).ShouldBe(T<Lead>());
            merged.StreamFor(T<AccountCreated>()).ShouldBe(T<Account>());

            // ContactCreated is no longer emitted once the code wins, so its lane goes with it
            merged.StreamFor(T<ContactCreated>()).ShouldBeNull();
        }
    }

    [Fact]
    public void two_declarations_putting_an_event_in_different_lanes_disagree()
    {
        var first = EventModelSliceDescriptor.Named("Convert") with
        {
            EmittedEvents = [T<AccountCreated>()],
            AggregateTypes = [T<Account>(), T<Lead>()],
            EmittedEventStreams = [new EmittedEventStream(T<AccountCreated>(), T<Account>())],
            Provenance = EventModelProvenance.Declared,
        };

        var second = first with
        {
            EmittedEventStreams = [new EmittedEventStream(T<AccountCreated>(), T<Lead>())],
        };

        var merged = first.Merge(second);

        // A tie keeps the first, and says what it dropped
        merged.StreamFor(T<AccountCreated>()).ShouldBe(T<Account>());
        merged.Hotspots.ShouldContain(x =>
            x.Origin == HotspotOrigin.SourceDisagreement && x.Text.Contains("AccountCreated on the Lead stream"));
    }

    public record ConvertLeadToAccount;
    public record LeadQualified;
    public record LeadConvertedToAccount;
    public record AccountCreated;
    public record ContactCreated;
    public record Lead;
    public record Account;
    public record Contact;
}
