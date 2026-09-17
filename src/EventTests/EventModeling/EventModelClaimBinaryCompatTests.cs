using System.Reflection;
using System.Text.Json;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#859 gave <see cref="EventModelClaim"/> a third, defaulted parameter, which is a
/// <em>source</em>-compatible change and a <em>binary</em>-breaking one — a record emits one
/// constructor, so the two-argument signature 2.72.0 published stopped existing. 2.73.1 restores it.
/// </summary>
/// <remarks>
/// These assertions have to go through reflection. <c>new EventModelClaim(rung, value)</c> in a test
/// would compile either way — the compiler would just bind it to the three-argument constructor and
/// fill in the default — so it proves nothing about the emitted surface, which is the only thing a
/// precompiled caller sees.
/// </remarks>
public class EventModelClaimBinaryCompatTests
{
    private static ConstructorInfo? Ctor(params Type[] parameters)
        => typeof(EventModelClaim).GetConstructor(parameters);

    [Fact]
    public void the_two_argument_constructor_2_72_0_published_still_exists()
    {
        Ctor(typeof(EventModelProvenance), typeof(string)).ShouldNotBeNull();
    }

    [Fact]
    public void it_behaves_as_an_unattributed_claim()
    {
        var claim = (EventModelClaim)Ctor(typeof(EventModelProvenance), typeof(string))!
            .Invoke([EventModelProvenance.Declared, "Automation"]);

        claim.Provenance.ShouldBe(EventModelProvenance.Declared);
        claim.Value.ShouldBe("Automation");
        claim.Source.ShouldBeNull();
        claim.Claimant.ShouldBe("Declared");
    }

    [Fact]
    public void it_is_equal_to_what_the_primary_constructor_builds_with_no_source()
    {
        var viaCompat = (EventModelClaim)Ctor(typeof(EventModelProvenance), typeof(string))!
            .Invoke([EventModelProvenance.Declared, "Automation"]);

        viaCompat.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "Automation", null));
    }

    [Fact]
    public void the_three_argument_constructor_is_still_the_primary_one()
    {
        Ctor(typeof(EventModelProvenance), typeof(string), typeof(string)).ShouldNotBeNull();
    }

    /// <summary>
    /// The trap the convenience constructor sets, pinned directly rather than only through the wire
    /// round-trip suite: a second parameterized constructor makes System.Text.Json refuse the type
    /// outright unless the primary one is marked <c>[JsonConstructor]</c>, and then every descriptor
    /// carrying a disagreement fails coming back off the wire.
    /// </summary>
    [Fact]
    public void a_claim_still_round_trips_through_system_text_json()
    {
        var attributed = new EventModelClaim(EventModelProvenance.Declared, "Automation",
            "file://CritterCrush.emodel.yaml");

        JsonSerializer.Deserialize<EventModelClaim>(JsonSerializer.Serialize(attributed))
            .ShouldBe(attributed);

        var anonymous = new EventModelClaim(EventModelProvenance.Derived, "Command");

        var back = JsonSerializer.Deserialize<EventModelClaim>(JsonSerializer.Serialize(anonymous))!;
        back.ShouldBe(anonymous);
        back.Source.ShouldBeNull();
    }

    [Fact]
    public void a_whole_disagreement_hotspot_still_round_trips()
    {
        var hotspot = HotspotDescriptor.SourceDisagreement(EventModelRole.Pattern,
            new EventModelClaim(EventModelProvenance.Declared, "Automation", "file://model.emodel.yaml"),
            new EventModelClaim(EventModelProvenance.Declared, "Command", "suite://Specs"));

        JsonSerializer.Deserialize<HotspotDescriptor>(JsonSerializer.Serialize(hotspot))
            .ShouldBe(hotspot);
    }

    /// <summary>
    /// The other half of the arity change, left alone on purpose: a two-output overload would
    /// silently drop <see cref="EventModelClaim.Source"/> for anyone destructuring a claim in new
    /// code, which is the loss jasperfx#859 exists to stop. Asserted so the decision is visible
    /// rather than looking like an oversight.
    /// </summary>
    [Fact]
    public void deconstruct_is_deliberately_not_restored()
    {
        var arities = typeof(EventModelClaim)
            .GetMethods()
            .Where(x => x.Name == "Deconstruct")
            .Select(x => x.GetParameters().Length)
            .ToList();

        arities.ShouldBe([3]);
    }
}
