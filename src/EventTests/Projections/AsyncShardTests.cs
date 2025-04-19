using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

public class AsyncShardTests
{
    [Fact]
    public void override_the_projection_name()
    {
        var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
            new ShardName("Foo", "All", 3), Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>(),
            new EventFilterable());

        shard = shard.OverrideProjectionName("Bar");
        
        shard.Name.Name.ShouldBe("Bar");
        shard.Name.ShardKey.ShouldBe("All");
        shard.Name.Version.ShouldBe(3U);
    }
}