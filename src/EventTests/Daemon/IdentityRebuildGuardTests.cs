using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#1201: what a store's RebuildSingleStreamAsync reads off a progression row to refuse a rebuild outside the
// daemon while another node is advancing the shard
public class IdentityRebuildGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static ShardState aRow(DateTimeOffset? lastUpdated, string? status = null, int? node = null)
        => new("Trip:All", 100) { LastUpdated = lastUpdated, AgentStatus = status, RunningOnNode = node };

    [Fact]
    public void a_row_written_within_the_window_is_live()
        => IdentityRebuildGuard.IsLive(aRow(Now.AddSeconds(-5)), Now, TimeSpan.FromSeconds(30)).ShouldBeTrue();

    [Fact]
    public void a_row_older_than_the_window_is_not_live()
        => IdentityRebuildGuard.IsLive(aRow(Now.AddMinutes(-5)), Now, TimeSpan.FromSeconds(30)).ShouldBeFalse();

    [Fact]
    public void a_row_that_was_never_written_is_not_live()
        => IdentityRebuildGuard.IsLive(aRow(null), Now, TimeSpan.FromSeconds(30)).ShouldBeFalse();

    [Fact]
    public void a_stopped_or_paused_agent_is_not_live_however_recent()
    {
        IdentityRebuildGuard.IsLive(aRow(Now, nameof(AgentStatus.Stopped)), Now, TimeSpan.FromSeconds(30))
            .ShouldBeFalse();
        IdentityRebuildGuard.IsLive(aRow(Now, nameof(AgentStatus.Paused)), Now, TimeSpan.FromSeconds(30))
            .ShouldBeFalse();
    }

    [Fact]
    public void a_live_row_refuses_with_a_named_reason_and_the_node()
    {
        var refused = Should.Throw<IdentityRebuildRefusedException>(() =>
            IdentityRebuildGuard.AssertNotLiveElsewhere("Trip", "trip-1", aRow(Now, "Running", 3), Now));

        refused.Reason.ShouldBe(IdentityRebuildRefusal.ShardLiveOnAnotherNode);
        refused.ProjectionName.ShouldBe("Trip");
        refused.Message.ShouldContain("node 3");
    }

    [Fact]
    public void no_row_or_a_quiet_row_is_not_refused()
    {
        IdentityRebuildGuard.AssertNotLiveElsewhere("Trip", "trip-1", null, Now);
        IdentityRebuildGuard.AssertNotLiveElsewhere("Trip", "trip-1", aRow(Now.AddHours(-1)), Now);
    }
}
