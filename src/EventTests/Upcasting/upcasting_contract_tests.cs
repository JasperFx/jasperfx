using System.Text.Json;
using JasperFx.Events;
using JasperFx.Events.Upcasting;
using Shouldly;

namespace EventTests.Upcasting;

#region Test events

public record CartOpenedV1(Guid CartId, Guid ClientId);

public record CartOpenedV2(Guid CartId, Guid ClientId, string Status);

#endregion

/// <summary>
/// A minimal in-memory store stand-in: the payload is JSON in memory, "the store's serializer" is
/// System.Text.Json, and the accessors record which read path a transformation actually took.
/// </summary>
public class StubUpcastPayload : IUpcastPayload
{
    private readonly string _json;

    public StubUpcastPayload(object data)
    {
        _json = JsonSerializer.Serialize(data, data.GetType());
    }

    public int SyncReads { get; private set; }
    public int AsyncReads { get; private set; }

    public T As<T>() where T : notnull
    {
        SyncReads++;
        return JsonSerializer.Deserialize<T>(_json)!;
    }

    public ValueTask<T> AsAsync<T>(CancellationToken token) where T : notnull
    {
        AsyncReads++;
        return new ValueTask<T>(JsonSerializer.Deserialize<T>(_json)!);
    }

    public JsonDocument AsJsonDocument()
    {
        SyncReads++;
        return JsonDocument.Parse(_json);
    }

    public ValueTask<JsonDocument> AsJsonDocumentAsync(CancellationToken token)
    {
        AsyncReads++;
        return new ValueTask<JsonDocument>(JsonDocument.Parse(_json));
    }
}

public class UpcastTransformationTests
{
    private static readonly Guid theCartId = Guid.NewGuid();
    private static readonly Guid theClientId = Guid.NewGuid();

    private static StubUpcastPayload aV1Payload() => new(new CartOpenedV1(theCartId, theClientId));

    [Fact]
    public void typed_transformation_claims_the_old_types_conventional_event_type_name()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            old => new CartOpenedV2(old.CartId, old.ClientId, "Opened"));

        transformation.EventTypeName.ShouldBe(EventTypeExtensions.GetEventTypeName<CartOpenedV1>());
        transformation.EventType.ShouldBe(typeof(CartOpenedV2));
    }

    [Fact]
    public void typed_transformation_honors_an_explicit_event_type_name()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            old => new CartOpenedV2(old.CartId, old.ClientId, "Opened"), "cart_opened_v1");

        transformation.EventTypeName.ShouldBe("cart_opened_v1");
    }

    [Fact]
    public void typed_transformation_upcasts_through_the_sync_path()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            old => new CartOpenedV2(old.CartId, old.ClientId, "Opened"));

        var payload = aV1Payload();
        var upcast = transformation.Upcast(payload).ShouldBeOfType<CartOpenedV2>();

        upcast.CartId.ShouldBe(theCartId);
        upcast.ClientId.ShouldBe(theClientId);
        upcast.Status.ShouldBe("Opened");
        payload.SyncReads.ShouldBe(1);
        payload.AsyncReads.ShouldBe(0);
    }

    [Fact]
    public async Task typed_transformation_uses_the_async_accessor_on_the_async_path()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            old => new CartOpenedV2(old.CartId, old.ClientId, "Opened"));

        var payload = aV1Payload();
        var upcast = (await transformation.UpcastAsync(payload, CancellationToken.None))
            .ShouldBeOfType<CartOpenedV2>();

        upcast.Status.ShouldBe("Opened");
        payload.AsyncReads.ShouldBe(1);
        payload.SyncReads.ShouldBe(0);
    }

    [Fact]
    public void async_only_typed_transformation_throws_on_the_sync_path()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            (old, _) => Task.FromResult(new CartOpenedV2(old.CartId, old.ClientId, "Opened")));

        Should.Throw<UpcastingException>(() => transformation.Upcast(aV1Payload()));
    }

    [Fact]
    public async Task async_only_typed_transformation_works_on_the_async_path()
    {
        var transformation = UpcastTransformation.For<CartOpenedV1, CartOpenedV2>(
            (old, _) => Task.FromResult(new CartOpenedV2(old.CartId, old.ClientId, "Opened")));

        var upcast = (await transformation.UpcastAsync(aV1Payload(), CancellationToken.None))
            .ShouldBeOfType<CartOpenedV2>();

        upcast.CartId.ShouldBe(theCartId);
    }

    [Fact]
    public void raw_json_transformation_defaults_to_the_new_types_event_type_name()
    {
        var transformation = UpcastTransformation.FromJson(
            document => new CartOpenedV2(
                document.RootElement.GetProperty("CartId").GetGuid(),
                document.RootElement.GetProperty("ClientId").GetGuid(),
                "Opened"));

        // The "same name, older JSON schema" case, matching Marten's SystemTextJson upcasters.
        transformation.EventTypeName.ShouldBe(EventTypeExtensions.GetEventTypeName<CartOpenedV2>());
    }

    [Fact]
    public void raw_json_transformation_upcasts_from_the_stored_json()
    {
        var transformation = UpcastTransformation.FromJson(
            document => new CartOpenedV2(
                document.RootElement.GetProperty("CartId").GetGuid(),
                document.RootElement.GetProperty("ClientId").GetGuid(),
                "Opened"),
            "cart_opened_v1");

        var upcast = transformation.Upcast(aV1Payload()).ShouldBeOfType<CartOpenedV2>();

        upcast.CartId.ShouldBe(theCartId);
        upcast.ClientId.ShouldBe(theClientId);
    }

    [Fact]
    public void async_only_raw_json_transformation_throws_on_the_sync_path()
    {
        var transformation = UpcastTransformation.FromJson(
            (JsonDocument document, CancellationToken _) => Task.FromResult(
                new CartOpenedV2(theCartId, theClientId, "Opened")));

        Should.Throw<UpcastingException>(() => transformation.Upcast(aV1Payload()));
    }

    [Fact]
    public async Task async_only_raw_json_transformation_works_on_the_async_path()
    {
        var transformation = UpcastTransformation.FromJson(
            (JsonDocument document, CancellationToken _) => Task.FromResult(
                new CartOpenedV2(
                    document.RootElement.GetProperty("CartId").GetGuid(),
                    document.RootElement.GetProperty("ClientId").GetGuid(),
                    "Opened")));

        var upcast = (await transformation.UpcastAsync(aV1Payload(), CancellationToken.None))
            .ShouldBeOfType<CartOpenedV2>();

        upcast.ClientId.ShouldBe(theClientId);
    }
}

public class ClassBasedUpcasterTests
{
    public class CartOpenedUpcaster : EventUpcaster<CartOpenedV1, CartOpenedV2>
    {
        protected override CartOpenedV2 Upcast(CartOpenedV1 oldEvent) =>
            new(oldEvent.CartId, oldEvent.ClientId, "Opened");
    }

    public class AsyncOnlyCartOpenedUpcaster : AsyncOnlyEventUpcaster<CartOpenedV1, CartOpenedV2>
    {
        protected override Task<CartOpenedV2> UpcastAsync(CartOpenedV1 oldEvent, CancellationToken token) =>
            Task.FromResult(new CartOpenedV2(oldEvent.CartId, oldEvent.ClientId, "Opened"));
    }

    public class RawJsonCartOpenedUpcaster : JasperFx.Events.Upcasting.SystemTextJson.EventUpcaster<CartOpenedV2>
    {
        public override string EventTypeName => "cart_opened_v1";

        protected override CartOpenedV2 Upcast(JsonDocument oldEvent) =>
            new(oldEvent.RootElement.GetProperty("CartId").GetGuid(),
                oldEvent.RootElement.GetProperty("ClientId").GetGuid(),
                "Opened");
    }

    private static readonly Guid theCartId = Guid.NewGuid();

    private static StubUpcastPayload aV1Payload() =>
        new(new CartOpenedV1(theCartId, Guid.NewGuid()));

    [Fact]
    public void typed_upcaster_uses_the_old_types_conventional_name_and_the_new_clr_type()
    {
        var upcaster = new CartOpenedUpcaster();

        upcaster.EventTypeName.ShouldBe(EventTypeExtensions.GetEventTypeName<CartOpenedV1>());
        upcaster.EventType.ShouldBe(typeof(CartOpenedV2));
    }

    [Fact]
    public void typed_upcaster_transforms_in_both_paths()
    {
        var upcaster = new CartOpenedUpcaster();

        upcaster.Upcast(aV1Payload()).ShouldBeOfType<CartOpenedV2>().CartId.ShouldBe(theCartId);
        upcaster.UpcastAsync(aV1Payload(), CancellationToken.None).Result
            .ShouldBeOfType<CartOpenedV2>().CartId.ShouldBe(theCartId);
    }

    [Fact]
    public async Task async_only_upcaster_throws_sync_and_works_async()
    {
        var upcaster = new AsyncOnlyCartOpenedUpcaster();

        Should.Throw<UpcastingException>(() => upcaster.Upcast(aV1Payload()));

        (await upcaster.UpcastAsync(aV1Payload(), CancellationToken.None))
            .ShouldBeOfType<CartOpenedV2>().CartId.ShouldBe(theCartId);
    }

    [Fact]
    public async Task raw_json_upcaster_transforms_from_the_stored_json()
    {
        var upcaster = new RawJsonCartOpenedUpcaster();

        upcaster.EventTypeName.ShouldBe("cart_opened_v1");
        upcaster.Upcast(aV1Payload()).ShouldBeOfType<CartOpenedV2>().CartId.ShouldBe(theCartId);
        (await upcaster.UpcastAsync(aV1Payload(), CancellationToken.None))
            .ShouldBeOfType<CartOpenedV2>().CartId.ShouldBe(theCartId);
    }
}

public class UpcastingRegistryTests
{
    private readonly UpcastingRegistry theRegistry = new();

    private static readonly string theOldName = EventTypeExtensions.GetEventTypeName<CartOpenedV1>();

    [Fact]
    public void starts_empty()
    {
        theRegistry.HasAny.ShouldBeFalse();
        theRegistry.TryFindTransformation(theOldName, out _).ShouldBeFalse();
        theRegistry.IsUpcastSource(theOldName).ShouldBeFalse();
    }

    [Fact]
    public void registers_a_typed_upcast_under_the_old_types_name()
    {
        theRegistry.Upcast<CartOpenedV1, CartOpenedV2>(
            old => new CartOpenedV2(old.CartId, old.ClientId, "Opened"));

        theRegistry.HasAny.ShouldBeTrue();
        theRegistry.IsUpcastSource(theOldName).ShouldBeTrue();

        theRegistry.TryFindTransformation(theOldName, out var transformation).ShouldBeTrue();
        transformation!.EventType.ShouldBe(typeof(CartOpenedV2));
    }

    [Fact]
    public void registration_is_last_wins_per_event_type_name()
    {
        theRegistry.Upcast<CartOpenedV1, CartOpenedV2>("cart_opened",
            old => new CartOpenedV2(old.CartId, old.ClientId, "First"));

        theRegistry.Upcast<CartOpenedV1, CartOpenedV2>("cart_opened",
            old => new CartOpenedV2(old.CartId, old.ClientId, "Second"));

        theRegistry.TryFindTransformation("cart_opened", out var transformation).ShouldBeTrue();

        var payload = new StubUpcastPayload(new CartOpenedV1(Guid.NewGuid(), Guid.NewGuid()));
        transformation!.Upcast(payload).ShouldBeOfType<CartOpenedV2>().Status.ShouldBe("Second");

        // Both registrations remain visible for stores pre-registering target types.
        theRegistry.AllTransformations.Count.ShouldBe(2);
    }

    [Fact]
    public void registers_class_based_upcasters()
    {
        theRegistry.Upcast<ClassBasedUpcasterTests.CartOpenedUpcaster>();

        theRegistry.IsUpcastSource(theOldName).ShouldBeTrue();
        theRegistry.TryFindTransformation(theOldName, out var transformation).ShouldBeTrue();
        transformation!.EventType.ShouldBe(typeof(CartOpenedV2));
    }

    [Fact]
    public void registers_upcaster_instances()
    {
        theRegistry.Upcast(new ClassBasedUpcasterTests.RawJsonCartOpenedUpcaster());

        theRegistry.IsUpcastSource("cart_opened_v1").ShouldBeTrue();
    }

    [Fact]
    public void raw_json_registration_defaults_to_the_new_types_name()
    {
        theRegistry.Upcast(document => new CartOpenedV2(
            document.RootElement.GetProperty("CartId").GetGuid(),
            document.RootElement.GetProperty("ClientId").GetGuid(),
            "Opened"));

        theRegistry.IsUpcastSource(EventTypeExtensions.GetEventTypeName<CartOpenedV2>()).ShouldBeTrue();
    }

    [Fact]
    public void the_shared_event_registry_exposes_an_upcasting_registry()
    {
        var registry = new EventRegistry();

        registry.Upcasters.ShouldNotBeNull();
        registry.Upcasters.ShouldBeSameAs(registry.Upcasters);
    }
}
