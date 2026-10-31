using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// The binary serializer registry lifted from Marten, Polecat and Fisher (#1043). The stores disagreed on
/// when a <see cref="BinaryEventAttribute" /> type with no serializer is refused, and two of them had
/// fallen over registration order, so most of these are about when an answer may change.
/// </summary>
public class EventRegistryBinarySerializerTests
{
    private readonly EventRegistry theRegistry = new();
    private readonly StubSerializer theSerializer = new();

    [Fact]
    public void a_plain_event_type_stays_on_the_json_path()
    {
        theRegistry.DefaultBinarySerializer = theSerializer;

        theRegistry.FindBinarySerializerFor(typeof(Plain)).ShouldBeNull();
        theRegistry.ResolveBinarySerializerFor(typeof(Plain)).ShouldBeNull();
    }

    [Fact]
    public void an_explicit_registration_needs_no_attribute()
    {
        theRegistry.UseBinarySerializer<Plain>(theSerializer);

        theRegistry.ResolveBinarySerializerFor(typeof(Plain)).ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void a_marked_type_falls_back_to_the_default()
    {
        theRegistry.DefaultBinarySerializer = theSerializer;

        theRegistry.ResolveBinarySerializerFor(typeof(Marked)).ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void an_explicit_registration_beats_the_attribute_and_the_default()
    {
        var explicitSerializer = new StubSerializer();

        theRegistry.DefaultBinarySerializer = theSerializer;
        theRegistry.UseBinarySerializer<Marked>(explicitSerializer);

        theRegistry.ResolveBinarySerializerFor(typeof(Marked)).ShouldBeSameAs(explicitSerializer);
    }

    [Fact]
    public void an_attribute_derived_from_the_promoted_one_counts()
    {
        // Marten's own [BinaryEvent] derives from the promoted attribute
        theRegistry.DefaultBinarySerializer = theSerializer;

        theRegistry.ResolveBinarySerializerFor(typeof(MarkedByStoreAttribute)).ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void the_attribute_is_not_inherited_by_a_subclass_of_the_event()
    {
        theRegistry.DefaultBinarySerializer = theSerializer;

        theRegistry.ResolveBinarySerializerFor(typeof(SubclassOfMarked)).ShouldBeNull();
    }

    [Fact]
    public void a_marked_type_with_no_serializer_is_refused_on_resolution()
    {
        Should.Throw<InvalidOperationException>(() => theRegistry.ResolveBinarySerializerFor(typeof(Marked)))
            .Message.ShouldContain("no IEventBinarySerializer was registered");
    }

    [Fact]
    public void a_marked_type_with_no_serializer_can_still_be_looked_up()
    {
        // Fisher's reason for its tolerant lookup: a mapping is built on read paths and before the
        // serializer is assigned, and neither is the place to refuse.
        theRegistry.FindBinarySerializerFor(typeof(Marked)).ShouldBeNull();
    }

    [Fact]
    public void the_default_may_arrive_after_the_refusal()
    {
        Should.Throw<InvalidOperationException>(() => theRegistry.ResolveBinarySerializerFor(typeof(Marked)));

        theRegistry.DefaultBinarySerializer = theSerializer;

        theRegistry.ResolveBinarySerializerFor(typeof(Marked)).ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void assigning_the_default_replaces_an_answer_already_given()
    {
        theRegistry.DefaultBinarySerializer = theSerializer;
        theRegistry.ResolveBinarySerializerFor(typeof(Marked)).ShouldBeSameAs(theSerializer);

        var replacement = new StubSerializer();
        theRegistry.DefaultBinarySerializer = replacement;

        theRegistry.ResolveBinarySerializerFor(typeof(Marked)).ShouldBeSameAs(replacement);
    }

    [Fact]
    public void an_explicit_registration_replaces_an_answer_already_given()
    {
        theRegistry.FindBinarySerializerFor(typeof(Plain)).ShouldBeNull();

        theRegistry.UseBinarySerializer<Plain>(theSerializer);

        theRegistry.FindBinarySerializerFor(typeof(Plain)).ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void an_explicit_registration_also_registers_the_event_type()
    {
        var registry = new StoreShapedRegistry();

        registry.UseBinarySerializer<Plain>(theSerializer);

        registry.Registered.ShouldContain(typeof(Plain));
    }

    [Fact]
    public void a_null_serializer_is_refused()
    {
        Should.Throw<ArgumentNullException>(() => theRegistry.UseBinarySerializer<Plain>(null!));
    }

    [Fact]
    public void an_event_built_before_either_registration_gets_the_serializer_through_the_hook()
    {
        // Marten and Fisher copy the serializer onto the event mapping, which can be built first by an
        // AddEventType or a projection. Fisher refreshed on both routes, Marten only on one.
        var registry = new StoreShapedRegistry();
        registry.AddEventType(typeof(Marked));
        registry.AddEventType(typeof(Plain));

        registry.CopiedOntoMapping[typeof(Marked)].ShouldBeNull();

        registry.DefaultBinarySerializer = theSerializer;
        registry.CopiedOntoMapping[typeof(Marked)].ShouldBeSameAs(theSerializer);

        registry.UseBinarySerializer<Plain>(theSerializer);
        registry.CopiedOntoMapping[typeof(Plain)].ShouldBeSameAs(theSerializer);
    }

    [Fact]
    public void the_write_path_serializes_a_binary_event()
    {
        theRegistry.UseBinarySerializer<Plain>(theSerializer);
        var data = new Plain();

        theRegistry.SerializeBinaryEventData(new Event<Plain>(data)).ShouldBe(StubSerializer.Bytes);

        theSerializer.Serialized.ShouldBe([(typeof(Plain), (object)data)]);
    }

    [Fact]
    public void the_write_path_leaves_a_json_event_alone()
    {
        theRegistry.SerializeBinaryEventData(new Event<Plain>(new Plain())).ShouldBeNull();
    }

    [Fact]
    public void the_write_path_refuses_a_marked_event_with_no_serializer()
    {
        // The one thing all three stores agreed on: never silently write JSON for it
        Should.Throw<InvalidOperationException>(() =>
            theRegistry.SerializeBinaryEventData(new Event<Marked>(new Marked())));
    }

    public class Plain;

    [BinaryEvent]
    public class Marked;

    public class SubclassOfMarked : Marked;

    public class StoreBinaryEventAttribute : BinaryEventAttribute;

    [StoreBinaryEvent]
    public class MarkedByStoreAttribute;

    public class StubSerializer : IEventBinarySerializer
    {
        public static readonly byte[] Bytes = [1, 2, 3];

        public List<(Type, object)> Serialized { get; } = [];

        public byte[] Serialize(Type type, object data)
        {
            Serialized.Add((type, data));
            return Bytes;
        }

        public object Deserialize(Type type, byte[] data) => throw new NotSupportedException();
    }

    private class StoreShapedRegistry : EventRegistry
    {
        public List<Type> Registered { get; } = [];

        public Dictionary<Type, IEventBinarySerializer?> CopiedOntoMapping { get; } = new();

        public override void AddEventType(Type eventType)
        {
            Registered.Add(eventType);
            CopiedOntoMapping[eventType] = FindBinarySerializerFor(eventType);
        }

        protected override void OnBinarySerializersChanged()
        {
            foreach (var eventType in CopiedOntoMapping.Keys.ToArray())
            {
                CopiedOntoMapping[eventType] = FindBinarySerializerFor(eventType);
            }
        }
    }
}
