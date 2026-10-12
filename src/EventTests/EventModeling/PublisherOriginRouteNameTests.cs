using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// <see cref="PublisherOrigin.HttpRouteName"/>: an HTTP endpoint's route name on the trigger origin, merged
/// like the route — a known name beats an unknown one, and two known names that differ disagree.
/// </summary>
public class PublisherOriginRouteNameTests
{
    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static PublisherOrigin origin(string? routeName) => new()
    {
        HttpMethod = "GET",
        HttpRoute = "/api/orders/{id}",
        HttpRouteName = routeName,
        Label = "GET /api/orders/{id}",
    };

    private static EventModelSliceDescriptor slice(EventModelProvenance provenance, string? routeName) =>
        EventModelSliceDescriptor.Named("GET /api/orders/{id}") with
        {
            Provenance = provenance,
            TriggerOrigin = origin(routeName),
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void the_route_name_round_trips(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
        var model = new EventModelDescriptor("Orders", [slice(EventModelProvenance.Derived, "GetOrder")]);

        var json = JsonSerializer.Serialize(model, options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

        back.Slices.Single().TriggerOrigin.ShouldBe(origin("GetOrder"));
        json.ShouldContain(critterWatchWire ? "\"httpRouteName\":\"GetOrder\"" : "\"HttpRouteName\":\"GetOrder\"");
    }

    [Fact]
    public void an_origin_from_before_the_route_name_still_deserializes()
    {
        const string json = """{"httpRoute":"/api/orders/{id}","httpMethod":"GET"}""";

        JsonSerializer.Deserialize<PublisherOrigin>(json, CritterWatchWire)!.HttpRouteName.ShouldBeNull();
    }

    [Fact]
    public void a_known_route_name_beats_an_unknown_one_from_a_higher_rung()
    {
        // The code wins the origin, but it could not see the name the declaration (or Bobcat) knew
        foreach (var declaredFirst in new[] { true, false })
        {
            var declared = slice(EventModelProvenance.Declared, "GetOrder");
            var derived = slice(EventModelProvenance.Derived, null);

            var merged = declaredFirst ? declared.Merge(derived) : derived.Merge(declared);

            merged.TriggerOrigin!.HttpRouteName.ShouldBe("GetOrder");
            merged.TriggerOrigin.HttpRoute.ShouldBe("/api/orders/{id}");
            merged.Hotspots.ShouldBeEmpty();
        }
    }

    [Fact]
    public void a_known_route_name_beats_an_unknown_one_on_the_same_rung()
    {
        var merged = slice(EventModelProvenance.Derived, null).Merge(slice(EventModelProvenance.Derived, "GetOrder"));

        merged.TriggerOrigin!.HttpRouteName.ShouldBe("GetOrder");
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void two_different_known_route_names_disagree()
    {
        var merged = slice(EventModelProvenance.Declared, "FetchOrder").Merge(slice(EventModelProvenance.Derived, "GetOrder"));

        merged.TriggerOrigin!.HttpRouteName.ShouldBe("GetOrder");

        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
        hotspot.Role.ShouldBe(EventModelRole.TriggerOrigin);
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "route name GetOrder"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "route name FetchOrder"));
    }

    [Fact]
    public void the_same_route_name_merges_silently()
    {
        var merged = slice(EventModelProvenance.Declared, "GetOrder").Merge(slice(EventModelProvenance.Derived, "GetOrder"));

        merged.TriggerOrigin!.HttpRouteName.ShouldBe("GetOrder");
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void a_route_name_is_not_borrowed_across_different_routes()
    {
        var declared = slice(EventModelProvenance.Declared, "GetOrder") with
        {
            TriggerOrigin = origin("GetOrder") with { HttpRoute = "/api/orders/{id}/lines", Label = "GET /api/orders/{id}/lines" },
        };

        var merged = declared.Merge(slice(EventModelProvenance.Derived, null));

        merged.TriggerOrigin!.HttpRouteName.ShouldBeNull();
        merged.Hotspots.ShouldHaveSingleItem().Role.ShouldBe(EventModelRole.TriggerOrigin);
    }
}
