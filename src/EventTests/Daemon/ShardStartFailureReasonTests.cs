using JasperFx;
using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#912 — <see cref="ShardStartException"/> carried its cause only as prose, so a caller could
/// tell a configuration error from a startup race only by matching English text. These pin the
/// classification: the four daemon cases map to four distinct <see cref="ShardStartFailureReason"/>
/// values, the retry/never-retry split is encoded rather than re-derived per consumer, and the messages
/// are unchanged.
/// </summary>
public class ShardStartFailureReasonTests
{
    private static readonly string[] theKnownShards = ["Trips:All", "Orders:All"];

    private static (ShardStartFailureReason Reason, string Message) classify(
        AgentStatus? registeredStatus = null,
        bool highWaterIsRunning = true,
        string identity = "Trips:All")
        => ShardStartFailureClassifier.Classify(registeredStatus, highWaterIsRunning,
            () => theKnownShards, identity);

    [Fact]
    public void a_registered_agent_in_a_non_running_status_is_a_pause()
    {
        var (reason, message) = classify(registeredStatus: AgentStatus.Paused);

        reason.ShouldBe(ShardStartFailureReason.AgentPaused);

        // The status is named, because the useful next step is reading the log for the pause reason.
        message.ShouldContain("Paused");
        message.ShouldContain("paused by an error");
    }

    [Fact]
    public void high_water_detection_not_running_is_transient()
    {
        var (reason, message) = classify(highWaterIsRunning: false);

        reason.ShouldBe(ShardStartFailureReason.HighWaterNotRunning);
        message.ShouldContain("High-water detection is not running yet");
    }

    [Fact]
    public void an_unregistered_shard_is_a_configuration_error()
    {
        var (reason, message) = classify(identity: "Nope:All");

        reason.ShouldBe(ShardStartFailureReason.ShardNotRegistered);

        // Naming the registry is the whole value of this case: the usual cause is a typo or a projection
        // that was never registered, and the known set is what settles which.
        message.ShouldContain("Trips:All, Orders:All");
    }

    [Fact]
    public void a_registered_shard_that_did_not_start_and_did_not_fault_is_a_race()
    {
        var (reason, message) = classify();

        reason.ShouldBe(ShardStartFailureReason.StartRace);
        message.ShouldContain("startup race between concurrent agent starts");
    }

    [Fact]
    public void the_registered_agent_check_wins_over_an_unregistered_identity()
    {
        // Order of specificity. An agent registered under an identity the store no longer lists is a
        // live agent to explain, not a configuration error to report — and reporting "no such shard" for
        // something this daemon is demonstrably holding an agent for would be actively misleading.
        var (reason, _) = classify(registeredStatus: AgentStatus.Stopped, identity: "Nope:All");

        reason.ShouldBe(ShardStartFailureReason.AgentPaused);
    }

    [Fact]
    public void high_water_wins_over_an_unregistered_identity()
    {
        // With detection still coming up, nothing could have been positioned regardless of the registry,
        // so the transient answer is the accurate one.
        var (reason, _) = classify(highWaterIsRunning: false, identity: "Nope:All");

        reason.ShouldBe(ShardStartFailureReason.HighWaterNotRunning);
    }

    [Theory]
    [InlineData(ShardStartFailureReason.HighWaterNotRunning)]
    [InlineData(ShardStartFailureReason.StartRace)]
    public void the_two_races_are_transient(ShardStartFailureReason reason)
    {
        new ShardStartException("Trips:All", "whatever", reason).IsTransient.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ShardStartFailureReason.Unknown)]
    [InlineData(ShardStartFailureReason.AgentPaused)]
    [InlineData(ShardStartFailureReason.ShardNotRegistered)]
    public void nothing_else_is_transient(ShardStartFailureReason reason)
    {
        // ShardNotRegistered is the one that matters: retrying it is the permanent retry loop of
        // wolverine#3519. Unknown is here because a consumer must never auto-retry a failure that was
        // never classified — the conservative default is the point of that member existing.
        new ShardStartException("Trips:All", "whatever", reason).IsTransient.ShouldBeFalse();
    }

    [Fact]
    public void a_faulted_start_carries_its_cause_and_is_not_transient()
    {
        var inner = new DivideByZeroException();
        var ex = new ShardStartException("Trips:All", inner);

        ex.Reason.ShouldBe(ShardStartFailureReason.Faulted);
        ex.InnerException.ShouldBeSameAs(inner);

        // Whether a retry helps is the inner exception's business, so this deliberately does not claim it.
        ex.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public void an_unclassified_exception_defaults_to_unknown()
    {
        // The prose-only constructor is what existed before #912, so it has to keep working — and it has
        // to not pretend to a classification it was not given.
        new ShardStartException("Trips:All", "some prose").Reason.ShouldBe(ShardStartFailureReason.Unknown);
    }

    [Fact]
    public void the_messages_are_unchanged()
    {
        // #912 adds a classification beside the prose; it does not restate the prose. Anything reading
        // these messages today (a log, an operator) sees exactly what it saw before.
        new ShardStartException("Trips:All", "Some reason.").Message
            .ShouldBe("Unable to start a subscription agent for 'Trips:All'. Some reason.");

        new ShardStartException("Trips:All", new DivideByZeroException()).Message
            .ShouldBe("Failure while trying to start 'Trips:All'");
    }
}
