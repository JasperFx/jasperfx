using JasperFx;
using Shouldly;

namespace CoreTests;

// jasperfx#795 — the shared LINQ refusal, lifted from three per-store copies. Pins the subclass seam
// that lets a store keep its own wording, since that is the whole reason the lift is safe to adopt.
public class BadLinqExpressionExceptionTests
{
    [Fact]
    public void carries_the_message_it_was_given()
    {
        new BadLinqExpressionException("cannot translate this")
            .Message.ShouldBe("cannot translate this");
    }

    [Fact]
    public void carries_an_inner_exception()
    {
        var inner = new InvalidOperationException("root cause");

        new BadLinqExpressionException("cannot translate this", inner)
            .InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void a_null_inner_exception_is_allowed()
    {
        // The stores' copies took a non-nullable inner; callers that had none used the one-arg form.
        // Nullable here so a throw site that forwards an optional inner does not need two branches.
        new BadLinqExpressionException("nope", null).InnerException.ShouldBeNull();
    }

    [Fact]
    public void expression_is_null_unless_a_subclass_supplies_it()
    {
        new BadLinqExpressionException("nope").Expression.ShouldBeNull();
    }

    [Fact]
    public void a_store_subclass_can_keep_its_own_wording_and_add_context()
    {
        // The seam jasperfx#756 established and this type repeats: a store whose tests pin its own
        // message adopts the shared type without changing what its users see.
        var ex = new StoreFlavoured("x => x.Date > DateTime.Now");

        ex.ShouldBeAssignableTo<BadLinqExpressionException>();
        ex.Message.ShouldContain("not sortable as text");
        ex.Expression.ShouldBe("x => x.Date > DateTime.Now");
    }

    private sealed class StoreFlavoured(string expression)
        : BadLinqExpressionException(
            $"A date member is not sortable as text: {expression}", null, expression);
}
