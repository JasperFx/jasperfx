using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
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

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe("Foo");
        descriptor.Version.ShouldBe(2U);
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        
        descriptor.ShardNames.ShouldBe([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        
        descriptor.Metrics.Select(x => x.Name).ShouldBe(["fake.foo.one.gap", "fake.foo.two.gap"]);
        descriptor.Metrics.Select(x => x.Type).Distinct().Single().ShouldBe(MetricsType.Histogram);
        
        descriptor.ActivitySpans.Select(x => x.Name).ShouldBe(["fake.foo.one.page.execution", "fake.foo.one.page.loading", "fake.foo.one.page.grouping", "fake.foo.two.page.execution", "fake.foo.two.page.loading", "fake.foo.two.page.grouping"]);
    }

    [Fact]
    public void serializable()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        
        descriptor.ShouldBeSerializable();
    }

    [Fact]
    public void descriptor_for_inline_should_have_no_metrics_or_activities()
    {
        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.Subscription);
        source.Name.Returns("Foo");
        source.Version.Returns(2U);
        source.ShardNames().Returns([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Inline);

        var store = new FakeEventStore();

        var descriptor = new SubscriptionDescriptor(source, store);
        descriptor.SubscriptionType.ShouldBe(SubscriptionType.Subscription);
        descriptor.Name.ShouldBe("Foo");
        descriptor.Version.ShouldBe(2U);
        descriptor.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
        
        descriptor.ShardNames.ShouldBe([new ShardName("Foo", "One", 2U), new ShardName("Foo", "Two", 2U)]);

        descriptor.Metrics.Any().ShouldBeFalse();
        descriptor.ActivitySpans.Any().ShouldBeFalse();
    }
}

public class FakeEventStore : IEventStore
{
    public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Uri Subject { get; } = "store://fake".ToUri();
    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
    {
        throw new NotImplementedException();
    }

    public Meter Meter { get; } = new Meter("Fake");
    public ActivitySource ActivitySource { get; } = new ActivitySource("Fake");
    public string MetricsPrefix { get; } = "fake";
    public DatabaseCardinality DatabaseCardinality { get; set; } = DatabaseCardinality.Single; 
    public bool HasMultipleTenants { get; set; }
}