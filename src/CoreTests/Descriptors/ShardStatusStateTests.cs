using JasperFx;
using JasperFx.Descriptors;
using Shouldly;
using Xunit;

namespace CoreTests.Descriptors;

/// <summary>
/// jasperfx#818. The vocabulary <see cref="ShardStatus.State" /> is drawn from, held to the two
/// things about it that are not self-evident from reading the constants.
/// </summary>
public class ShardStatusStateTests
{
    /// <summary>
    /// Three of the four are an <see cref="AgentStatus" /> spelled as a string, which is what makes
    /// <c>State</c> round-trippable against a daemon's own answer. Written as a loop over the enum so
    /// a member added to <see cref="AgentStatus" /> later fails here rather than silently becoming a
    /// state no consumer recognizes.
    /// </summary>
    [Fact]
    public void every_agent_status_has_a_state_string()
    {
        foreach (var status in Enum.GetValues<AgentStatus>())
        {
            ShardStatusState.All.ShouldContain(ShardStatusState.From(status));
        }
    }

    /// <summary>
    /// <see cref="ShardStatusState.Unknown" /> is the one value with no <see cref="AgentStatus" />
    /// counterpart, and that is the point of it: "there is no daemon here to ask" is not a state any
    /// agent is ever in, and it is specifically not <see cref="ShardStatusState.Stopped" />.
    /// </summary>
    [Fact]
    public void unknown_is_not_an_agent_status()
    {
        Enum.GetNames<AgentStatus>().ShouldNotContain(ShardStatusState.Unknown);

        ShardStatusState.Unknown.ShouldNotBe(ShardStatusState.Stopped);
    }

    [Fact]
    public void the_vocabulary_is_exactly_these_four()
    {
        ShardStatusState.All.ShouldBe(["Running", "Paused", "Stopped", "Unknown"]);
    }
}
