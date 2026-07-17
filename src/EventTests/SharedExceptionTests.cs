using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests;

public class SharedExceptionTests
{
    [Fact]
    public void concurrency_exception_preserves_an_inner_exception()
    {
        // Stores that detect the violation through their own exception (a CosmosDB 412 Precondition
        // Failed, say) need to keep it attached, so error handling policies and logs can still see what
        // the database actually said. Without a base ctor taking one, a derived exception cannot set
        // InnerException at all - it is only settable through Exception(string, Exception).
        var inner = new InvalidOperationException("412 Precondition Failed");
        var ex = new ConcurrencyException("Saga was modified concurrently", inner);

        ex.InnerException.ShouldBeSameAs(inner);
        ex.Message.ShouldBe("Saga was modified concurrently");
    }

    [Fact]
    public void dcb_concurrency_exception_carries_query_and_sequence_and_is_a_concurrency_exception()
    {
        var query = new EventTagQuery().Or<Guid>(Guid.NewGuid());
        var ex = new DcbConcurrencyException(query, 42);

        ex.ShouldBeAssignableTo<ConcurrencyException>();
        ex.Query.ShouldBeSameAs(query);
        ex.LastSeenSequence.ShouldBe(42);
        ex.Message.ShouldContain("sequence 42");
    }

    [Fact]
    public void progression_out_of_order_preserves_the_shardname_ctor()
    {
        var ex = new ProgressionProgressOutOfOrderException(new ShardName("Trip", "All", 1));

        ex.Message.ShouldContain("out of order");
        // The richer props default when constructed via the ShardName ctor.
        ex.ProjectionName.ShouldBeNull();
        ex.ExpectedFloor.ShouldBe(0);
        ex.AttemptedCeiling.ShouldBe(0);
    }

    [Fact]
    public void progression_out_of_order_folds_in_the_three_arg_ctor()
    {
        var ex = new ProgressionProgressOutOfOrderException("Trip", expectedFloor: 100, ceiling: 150);

        ex.ProjectionName.ShouldBe("Trip");
        ex.ExpectedFloor.ShouldBe(100);
        ex.AttemptedCeiling.ShouldBe(150);
        ex.Message.ShouldContain("Trip");
        ex.Message.ShouldContain("100");
        ex.Message.ShouldContain("150");
    }
}
