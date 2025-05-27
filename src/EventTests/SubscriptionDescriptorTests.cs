using System.Text.Json;
using EventTests.Descriptors;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace EventTests;

public class SubscriptionDescriptorTests
{
    [Fact]
    public void build_from_source()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Lifecycle.Returns(ProjectionLifecycle.Async);
        source.Name.Returns("Subscription1");
        source.ShardNames().Returns([new ShardName("Subscription1")]);
        source.Type.Returns(SubscriptionType.Subscription);
        source.Version.Returns(1U);
        source.ImplementationType.Returns(GetType());

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe(source.Name);
        descriptor.Version.ShouldBe(source.Version);
        descriptor.ShardNames.ShouldBe(source.ShardNames());
        descriptor.Lifecycle.ShouldBe(source.Lifecycle);

        descriptor.ShouldBeSerializable();
    }    
}

public static class SerializationTestExtensions
{
    public static void ShouldBeSerializable<T>(this T value)
    {
        var json = JsonSerializer.Serialize(value);
        var value2 = JsonSerializer.Deserialize<T>(json);
        var json2 = JsonSerializer.Serialize(value2);
    }
}