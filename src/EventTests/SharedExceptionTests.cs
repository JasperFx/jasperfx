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
