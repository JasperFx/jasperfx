using JasperFx.Events;
using Shouldly;

namespace EventTests;

// jasperfx#764: the natural key uniqueness ruling. Fisher's DuplicateNaturalKeyException is lifted
// beside the jasperfx#751 exceptions with its message adopted verbatim, so Fisher's swap is a
// type-forward rather than a subclass. The tests below pin the canonical shape a store has to keep
// expressible.
public class DuplicateNaturalKeyExceptionTests
{
    [Fact]
    public void carries_the_aggregate_type_and_the_key()
    {
        var ex = new DuplicateNaturalKeyException(typeof(Order), "ORD-1234");

        ex.AggregateType.ShouldBe(typeof(Order));
        ex.Key.ShouldBe("ORD-1234");

        ex.Message.ShouldBe(
            "The natural key 'ORD-1234' is already mapped to a different stream for aggregate type "
            + "'Order'. A natural key identifies one stream; if the mapping is meant to move, delete "
            + "the existing row first.");

        // Fisher's throw site infers the conflict from a write that affected no rows, so it has
        // neither stream id in hand. Both stay optional.
        ex.ExistingStreamId.ShouldBeNull();
        ex.ClaimingStreamId.ShouldBeNull();
    }

    [Fact]
    public void carries_both_stream_ids_when_the_throw_site_knew_them()
    {
        // The shape a store takes when it probes the lookup before writing rather than reacting to
        // an empty result — it knows who holds the key and who tried to take it.
        var existing = Guid.NewGuid();
        var claiming = Guid.NewGuid();

        var ex = new DuplicateNaturalKeyException(typeof(Order), "ORD-1234", existing, claiming);

        ex.ExistingStreamId.ShouldBe(existing);
        ex.ClaimingStreamId.ShouldBe(claiming);
        ex.Key.ShouldBe("ORD-1234");
    }

    [Fact]
    public void a_store_subclass_can_keep_its_diverged_message()
    {
        var ex = new StoreishDuplicateNaturalKeyException(typeof(Order), "ORD-1234");

        ex.Message.ShouldBe("Natural key ORD-1234 is taken");
        ex.AggregateType.ShouldBe(typeof(Order));
        ex.Key.ShouldBe("ORD-1234");
        ex.ShouldBeAssignableTo<DuplicateNaturalKeyException>();
    }

    private class StoreishDuplicateNaturalKeyException : DuplicateNaturalKeyException
    {
        public StoreishDuplicateNaturalKeyException(Type aggregateType, object key)
            : base($"Natural key {key} is taken", aggregateType, key, null, null)
        {
        }
    }

    private class Order;
}
