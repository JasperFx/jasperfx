using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace EventTests.Descriptors;

public class SubscriptionDescriptorTests
{
    [Fact]
    public void fill_from_subject()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var descriptor = new SubscriptionDescriptor(source);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe("Foo");
        descriptor.Version.ShouldBe(2U);
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        
        descriptor.ShardNames.ShouldBe([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
    }
}