using JasperFx.Testing;
using Shouldly;
using Xunit;

namespace CoreTests.Testing;

/// <summary>
/// These attributes carry no behaviour, so what is worth testing is the contract a runner reads
/// them through: the disposition each one declares, that resources parse, and that they can be
/// discovered from every scope they claim to support.
/// </summary>
public class RecoveryHintAttributeTests
{
    [Fact]
    public void each_hint_declares_the_disposition_its_name_promises()
    {
        new ClearsOnRetryAttribute(typeof(TimeoutException)).Kind
            .ShouldBe(DispositionKind.RetryInProcess);

        new ClearsInFreshProcessAttribute(typeof(BadImageFormatException)).Kind
            .ShouldBe(DispositionKind.RetryInFreshProcess);

        new ClearsOnRecycleAttribute("rabbit", typeof(TimeoutException)).Kind
            .ShouldBe(DispositionKind.RetryAfterRecycle);

        // The counterweight: a hint that spends no attempts at all.
        new NeverRecoversAttribute(typeof(InvalidOperationException)).Kind
            .ShouldBe(DispositionKind.FailAndContinue);
    }

    [Fact]
    public void the_failure_type_and_reason_are_carried_verbatim()
    {
        var hint = new ClearsOnRetryAttribute(typeof(TimeoutException))
        {
            Because = "the broker is slow to warm up"
        };

        hint.FailureType.ShouldBe(typeof(TimeoutException));

        // Reaches a run report unchanged, so it must not be normalised or truncated here.
        hint.Because.ShouldBe("the broker is slow to warm up");
    }

    [Fact]
    public void a_hint_with_no_reason_given_simply_has_none()
    {
        new ClearsOnRetryAttribute(typeof(TimeoutException)).Because.ShouldBeNull();
    }

    [Theory]
    [InlineData("rabbit", new[] { "rabbit" })]
    [InlineData("rabbit,kafka", new[] { "rabbit", "kafka" })]
    [InlineData(" rabbit , kafka ", new[] { "rabbit", "kafka" })]
    [InlineData("rabbit,,kafka", new[] { "rabbit", "kafka" })]
    public void recycle_resources_are_split_trimmed_and_compacted(string declared, string[] expected)
    {
        // Comma-separated to match the recycle(rabbit,kafka) tag vocabulary. Whitespace and empty
        // entries are the author's typos, not resource names — a runner asked to recycle "" would
        // report a wiring mistake for something nobody meant to write.
        new ClearsOnRecycleAttribute(declared, typeof(TimeoutException)).Resources.ShouldBe(expected);
    }

    [Fact]
    public void an_empty_recycle_list_is_empty_rather_than_a_single_blank_resource()
    {
        new ClearsOnRecycleAttribute("", typeof(TimeoutException)).Resources.ShouldBeEmpty();
        new ClearsOnRecycleAttribute("   ", typeof(TimeoutException)).Resources.ShouldBeEmpty();
    }

    [Fact]
    public void hints_other_than_recycle_name_no_resources()
    {
        new ClearsOnRetryAttribute(typeof(TimeoutException)).Resources.ShouldBeEmpty();
        new NeverRecoversAttribute(typeof(TimeoutException)).Resources.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- discovery

    [ClearsOnRetry(typeof(TimeoutException), Because = "warm-up")]
    [NeverRecovers(typeof(InvalidOperationException))]
    private class Annotated
    {
        [ClearsInFreshProcess(typeof(BadImageFormatException))]
        public void Method() { }
    }

    [Fact]
    public void several_hints_can_be_declared_on_one_target()
    {
        // AllowMultiple: a class routinely has more than one failure worth describing, and the
        // whole point is that each is described separately rather than lumped into "flaky".
        var hints = typeof(Annotated)
            .GetCustomAttributes(typeof(RecoveryHintAttribute), inherit: true)
            .Cast<RecoveryHintAttribute>()
            .ToList();

        hints.Count.ShouldBe(2);
        hints.Select(h => h.FailureType)
            .ShouldBe([typeof(TimeoutException), typeof(InvalidOperationException)], ignoreOrder: true);
    }

    [Fact]
    public void a_hint_is_discoverable_on_a_method_as_well_as_a_class()
    {
        // Scope is how a narrow declaration overrides a broad one, so a runner has to be able to
        // find them at every level the attribute claims to support.
        var hint = typeof(Annotated)
            .GetMethod(nameof(Annotated.Method))!
            .GetCustomAttributes(typeof(RecoveryHintAttribute), inherit: true)
            .Cast<RecoveryHintAttribute>()
            .ShouldHaveSingleItem();

        hint.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
    }

    [Fact]
    public void the_attribute_targets_cover_assembly_class_and_method()
    {
        var usage = (AttributeUsageAttribute)typeof(RecoveryHintAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .Single();

        usage.ValidOn.ShouldBe(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly);
        usage.AllowMultiple.ShouldBeTrue();
    }
}
