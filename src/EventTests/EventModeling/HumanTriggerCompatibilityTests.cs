using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// A declared <see cref="TriggerKind.Human"/> answers <em>who</em> triggers a slice; a derived
/// <see cref="TriggerKind.Http"/>, <see cref="TriggerKind.Grpc"/> or <see cref="TriggerKind.MessageHandler"/>
/// answers <em>how</em> the code receives it. The two are compatible: the merge keeps the how on
/// <see cref="EventModelSliceDescriptor.TriggerKind"/>, the who on
/// <see cref="EventModelSliceDescriptor.TriggeredBy"/>, and raises no hotspot.
/// </summary>
public class HumanTriggerCompatibilityTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static EventModelSliceDescriptor declared(TriggerKind kind) =>
        EventModelSliceDescriptor.Named("CloseIncident") with
        {
            Provenance = EventModelProvenance.Declared,
            TriggerLabel = "Agent clicks Close",
            TriggerKind = kind,
        };

    private static EventModelSliceDescriptor derived(TriggerKind kind) =>
        new EventModelSliceDescriptor("CloseIncident", null, null, T<CloseIncident>(), T<CloseIncidentEndpoint>(),
            [T<IncidentClosed>()], [], [])
        {
            Provenance = EventModelProvenance.Derived,
            Pattern = SlicePattern.Command,
            TriggerKind = kind,
            HandlerMethod = "Post",
            TriggerOrigin = kind == TriggerKind.Http
                ? new PublisherOrigin { HttpMethod = "POST", HttpRoute = "/api/incidents/close", Label = "POST /api/incidents/close" }
                : null,
        };

    // The compatibility table in docs/event-modeling/descriptors.md, pinned pair by pair.
    public static IEnumerable<object[]> Table() =>
    [
        [TriggerKind.Human, TriggerKind.Http, true],
        [TriggerKind.Human, TriggerKind.Grpc, true],
        [TriggerKind.Human, TriggerKind.MessageHandler, true],
        [TriggerKind.Human, TriggerKind.Human, true],
        [TriggerKind.Human, TriggerKind.JobScheduler, false],
        [TriggerKind.Human, TriggerKind.External, false],
        [TriggerKind.Http, TriggerKind.MessageHandler, false],
        [TriggerKind.Http, TriggerKind.Grpc, false],
        [TriggerKind.Grpc, TriggerKind.MessageHandler, false],
        [TriggerKind.MessageHandler, TriggerKind.JobScheduler, false],
        [TriggerKind.MessageHandler, TriggerKind.External, false],
        [TriggerKind.JobScheduler, TriggerKind.External, false],
    ];

    [Theory]
    [MemberData(nameof(Table))]
    public void the_compatibility_table(TriggerKind first, TriggerKind second, bool compatible)
    {
        first.IsCompatibleWith(second).ShouldBe(compatible);
        second.IsCompatibleWith(first).ShouldBe(compatible);
    }

    [Fact]
    public void every_trigger_kind_is_compatible_with_itself_and_only_the_receiving_kinds_take_a_human()
    {
        foreach (var kind in Enum.GetValues<TriggerKind>())
        {
            kind.IsCompatibleWith(kind).ShouldBeTrue();
        }

        Enum.GetValues<TriggerKind>().Where(x => x.CanBeHumanTriggered())
            .ShouldBe([TriggerKind.Http, TriggerKind.Grpc, TriggerKind.MessageHandler]);
    }

    [Theory]
    [InlineData(TriggerKind.Http)]
    [InlineData(TriggerKind.Grpc)]
    [InlineData(TriggerKind.MessageHandler)]
    public void a_declared_human_met_by_a_receiving_kind_keeps_both_facts_and_raises_no_hotspot(TriggerKind how)
    {
        foreach (var declaredFirst in new[] { true, false })
        {
            var merged = declaredFirst
                ? declared(TriggerKind.Human).Merge(derived(how))
                : derived(how).Merge(declared(TriggerKind.Human));

            merged.Hotspots.ShouldBeEmpty();

            // how: the derived kind, with its origin and the user's handler
            merged.TriggerKind.ShouldBe(how);
            merged.ProvenanceFor(EventModelRole.TriggerKind).ShouldBe(EventModelProvenance.Derived);
            merged.TriggerOrigin.ShouldBe(derived(how).TriggerOrigin);
            merged.HandlerType.ShouldBe(T<CloseIncidentEndpoint>());
            merged.HandlerMethod.ShouldBe("Post");

            // who: the declared Human
            merged.TriggeredBy.ShouldBe(TriggerKind.Human);
            merged.ProvenanceFor(EventModelRole.TriggeredBy).ShouldBe(EventModelProvenance.Declared);
            merged.IsTriggeredByHuman().ShouldBeTrue();
            merged.TriggerLabel.ShouldBe("Agent clicks Close");
        }
    }

    [Theory]
    [InlineData(TriggerKind.JobScheduler)]
    [InlineData(TriggerKind.External)]
    public void a_declared_human_met_by_automation_still_disagrees(TriggerKind automation)
    {
        var merged = declared(TriggerKind.Human).Merge(derived(automation));

        merged.TriggerKind.ShouldBe(automation);
        merged.TriggeredBy.ShouldBeNull();
        merged.IsTriggeredByHuman().ShouldBeFalse();

        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
        hotspot.Role.ShouldBe(EventModelRole.TriggerKind);
        hotspot.WinningClaim!.Value.ShouldBe(automation.ToString());
        hotspot.LosingClaim!.Value.ShouldBe("Human");
    }

    [Fact]
    public void two_different_receiving_kinds_still_disagree()
    {
        var merged = declared(TriggerKind.MessageHandler).Merge(derived(TriggerKind.Http));

        merged.TriggerKind.ShouldBe(TriggerKind.Http);
        merged.TriggeredBy.ShouldBeNull();
        merged.Hotspots.ShouldHaveSingleItem().Role.ShouldBe(EventModelRole.TriggerKind);
    }

    [Fact]
    public void a_lone_declaration_keeps_human_on_the_trigger_kind()
    {
        var slice = declared(TriggerKind.Human);

        slice.TriggerKind.ShouldBe(TriggerKind.Human);
        slice.TriggeredBy.ShouldBeNull();
        slice.IsTriggeredByHuman().ShouldBeTrue();
    }

    [Fact]
    public void a_split_slice_merges_with_another_human_declaration_silently()
    {
        // A model file and its specs both declare Human; the code says Http
        var merged = declared(TriggerKind.Human).Merge(derived(TriggerKind.Http)).Merge(declared(TriggerKind.Human));

        merged.Hotspots.ShouldBeEmpty();
        merged.TriggerKind.ShouldBe(TriggerKind.Http);
        merged.TriggeredBy.ShouldBe(TriggerKind.Human);
    }

    [Fact]
    public void a_split_slice_met_by_a_conflicting_how_disagrees_on_the_how_and_keeps_the_who()
    {
        var observed = derived(TriggerKind.MessageHandler) with { Provenance = EventModelProvenance.Observed };

        var merged = declared(TriggerKind.Human).Merge(derived(TriggerKind.Http)).Merge(observed);

        merged.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        merged.TriggeredBy.ShouldBe(TriggerKind.Human);
        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.TriggerKind);
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Observed, "MessageHandler"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "Http"));
    }

    [Fact]
    public async Task a_board_declared_human_command_assembles_against_the_code_without_a_hotspot()
    {
        var services = new ServiceCollection()
            .AddEventModel("Helpdesk", model =>
            {
                model.Slice("CloseIncident").TriggeredBy("Agent clicks Close", TriggerKind.Human);
            })
            .AddEventModelSource(new DerivedSource())
            .BuildServiceProvider();

        var slice = (await EventModelDiscovery.AssembleAsync(services, TestContext.Current.CancellationToken)).Single().Slices.Single();

        slice.Hotspots.ShouldBeEmpty();
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggeredBy.ShouldBe(TriggerKind.Human);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/api/incidents/close");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void triggered_by_round_trips(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
        var merged = declared(TriggerKind.Human).Merge(derived(TriggerKind.Http));

        var json = JsonSerializer.Serialize(new EventModelDescriptor("Helpdesk", [merged]), options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!.Slices.Single();

        json.ShouldContain(critterWatchWire ? "\"triggeredBy\":\"human\"" : "\"TriggeredBy\":4");
        back.TriggerKind.ShouldBe(TriggerKind.Http);
        back.TriggeredBy.ShouldBe(TriggerKind.Human);
        back.IsTriggeredByHuman().ShouldBeTrue();
    }

    [Fact]
    public void a_slice_from_before_triggered_by_still_deserializes()
    {
        const string json = """{"name":"CloseIncident","triggerKind":"http"}""";

        var slice = JsonSerializer.Deserialize<EventModelSliceDescriptor>(json, CritterWatchWire)!;

        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggeredBy.ShouldBeNull();
        slice.HandlerMethod.ShouldBeNull();
    }

    [Fact]
    public void the_triggered_by_role_is_appended_after_every_existing_role()
    {
        // EventModelRole goes over the wire as an integer under the STJ defaults
        ((int)EventModelRole.TriggeredBy).ShouldBe((int)EventModelRole.AggregateIdentity + 1);
    }

    private sealed class DerivedSource : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://derived");
        public EventModelProvenance Provenance => EventModelProvenance.Derived;

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(new EventModelDescriptor("Helpdesk", [derived(TriggerKind.Http)]));
    }

    public record CloseIncident;
    public class CloseIncidentEndpoint;
    public record IncidentClosed;
}
