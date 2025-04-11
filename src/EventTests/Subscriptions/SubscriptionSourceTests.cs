using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Shouldly;

namespace EventTests.Subscriptions;

public class SubscriptionSourceTests
{
    [Fact]
    public void build_shards()
    {
        var source = new FakeSubscriptionSource
        {
            SubscriptionName = "Foo"
        };
        
        source.SubscriptionVersion.ShouldBe(1U);

        var shard = source.Shards().Single();
        
        shard.Role.ShouldBe(ShardRole.Subscription);
        shard.Name.ShouldBe(new ShardName("Foo", "All"));
        shard.Factory.ShouldBe(source);
        shard.Filters.ShouldBe(source);
    }

    [Fact]
    public void build_shard_with_version_2()
    {
        var source = new FakeSubscriptionSource
        {
            SubscriptionName = "Foo",
            SubscriptionVersion = 2
        };
        
        source.SubscriptionVersion.ShouldBe(2U);

        var shard = source.Shards().Single();
        
        shard.Role.ShouldBe(ShardRole.Subscription);
        shard.Name.ShouldBe(new ShardName("Foo", "All", 2));
        shard.Name.Version.ShouldBe(2U);
        shard.Factory.ShouldBe(source);
        shard.Filters.ShouldBe(source);
    }
}



public interface IFakeSubscription
{
    
}

public class FakeSubscriptionSource : JasperFxSubscriptionBase<FakeOperations, FakeSession, IFakeSubscription>,
    IFakeSubscription
{
    
}