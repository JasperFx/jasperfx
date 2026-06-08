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

    [Fact]
    public void override_the_projection_name_preserves_tenant_binding()
    {
        // jasperfx#419: renaming the projection must not silently drop a tenant binding established
        // upstream (e.g. by the per-tenant catch-up loop). Losing it collapses every tenant onto the
        // same store-global progression row -> marten#4679 (23505 duplicate key).
        var shard = new AsyncShard<FakeOperations, FakeSession>(new AsyncOptions(), ShardRole.Projection,
            ShardName.Compose("Foo", "All", "tenant1", 3),
            Substitute.For<ISubscriptionFactory<FakeOperations, FakeSession>>(), new EventFilterable());

        shard = shard.OverrideProjectionName("Bar");

        shard.Name.Name.ShouldBe("Bar");
        shard.Name.TenantId.ShouldBe("tenant1");
        shard.Name.Identity.ShouldBe("Bar:V3:All:tenant1");
    }
}