using EventTests.Descriptors;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#1052: <see cref="IEventStore" /> members a consumer generates code from. The default for each is
/// to refuse, because a guessed answer here is wrong code that compiles.
/// </summary>
public class StoreFactDefaultTests
{
    public class Thing
    {
        public Guid Id { get; set; }
    }

    private static void shouldBeRefusedNaming<TImplementer>(Action act)
    {
        var refused = Should.Throw<NotSupportedException>(act);
        refused.Message.ShouldContain(typeof(TImplementer).Name);
    }

    [Fact]
    public void a_non_generic_store_refuses_stream_identity_rather_than_assuming_guid()
    {
        IEventStore store = new FakeEventStore();

        shouldBeRefusedNaming<FakeEventStore>(() => _ = store.StreamIdentity);
    }

    [Fact]
    public void identity_type_refuses_rather_than_guessing_from_an_id_member()
    {
        // Reflecting over Id is what Wolverine does for Polecat and Fisher, and a store can configure a
        // different identity: wolverine GH-4441
        IEventStore store = new FakeEventStore();

        shouldBeRefusedNaming<FakeEventStore>(() => store.IdentityTypeFor(typeof(Thing)));
    }

    [Fact]
    public void natural_key_refuses_rather_than_answering_null()
    {
        // Null means "no natural key", and inheriting that null is what made Fisher's natural-key branch
        // unreachable: wolverine GH-4439
        IEventStore store = new FakeEventStore();

        shouldBeRefusedNaming<FakeEventStore>(() => store.FindNaturalKeyDefinition(typeof(Thing)));
    }
}
