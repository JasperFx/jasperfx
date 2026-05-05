using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace EventTests.Descriptors;

public class EventStoreUsageTests
{
    [Fact]
    public void get_the_version_from_the_subject()
    {
        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing());
        usage.Version.ShouldBe(GetType().Assembly.GetName().Version?.ToString());
    }

    [Fact]
    public void ctor_does_not_reflect_subject_into_properties_or_children()
    {
        // Pin the Critter Stack #104 fix: constructing EventStoreUsage from a
        // live EventStore subject must NOT auto-walk the subject into
        // OptionsDescription.Children / .Properties. Callers populate the
        // first-class fields explicitly; the auto-walk dumps Storage /
        // Advanced / Diagnostics / Options handles into the descriptor and
        // those bleed into CritterWatch's Configuration sections as noise.
        var usage = new EventStoreUsage(new Uri("marten://main"), new SubjectWithLeakyHandles());

        // FullNameInCode renders nested types with `.` (vs Type.FullName's `+`),
        // so pin against that.
        usage.Subject.ShouldBe(typeof(SubjectWithLeakyHandles).FullNameInCode());
        usage.SubjectUri.ShouldBe(new Uri("marten://main"));
        usage.Version.ShouldBe(typeof(SubjectWithLeakyHandles).Assembly.GetName().Version?.ToString());

        usage.Properties.ShouldBeEmpty();
        usage.Children.ShouldBeEmpty();
        usage.Sets.ShouldBeEmpty();

        usage.Events.ShouldBeEmpty();
        usage.Subscriptions.ShouldBeEmpty();
        usage.TagTypes.ShouldBeEmpty();
        usage.GlobalAggregates.ShouldBeEmpty();
    }

    [Fact]
    public void ctor_throws_for_null_subject()
    {
        Should.Throw<ArgumentNullException>(
            () => new EventStoreUsage(new Uri("marten://main"), null!));
    }

    /// <summary>
    /// Stand-in for Marten's EventStore — exposes the kind of runtime
    /// handles (Storage / Advanced / Diagnostics / Options) that used to
    /// leak into the descriptor's Children dictionary before #104.
    /// </summary>
    private class SubjectWithLeakyHandles
    {
        public string Storage { get; } = "irrelevant";
        public string Advanced { get; } = "irrelevant";
        public string Diagnostics { get; } = "irrelevant";
        public string Options { get; } = "irrelevant";
    }

    [Fact]
    public void populate_agent_uris_for_async_subscriptions()
    {
        var store = new FakeEventStore();
        var identity = new EventStoreIdentity("main", "marten");

        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.SingleStreamProjection);
        source.Name.Returns("Trip");
        source.Version.Returns(1U);
        source.ShardNames().Returns([new ShardName("Trip", "All", 1U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing())
        {
            Database = new DatabaseUsage
            {
                MainDatabase = new DatabaseDescriptor
                {
                    ServerName = "localhost",
                    DatabaseName = "postgres"
                }
            }
        };

        usage.Subscriptions.Add(new SubscriptionDescriptor(source, store));

        usage.PopulateAgentUris("event-subscriptions", identity);

        var subscription = usage.Subscriptions[0];
        subscription.AgentUris.Length.ShouldBe(1);
        subscription.AgentUris[0].ShouldBe("event-subscriptions://marten/main/localhost.postgres/trip/all");
    }

    [Fact]
    public void populate_agent_uris_with_multiple_databases()
    {
        var store = new FakeEventStore();
        var identity = new EventStoreIdentity("main", "marten");

        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.SingleStreamProjection);
        source.Name.Returns("Day");
        source.Version.Returns(1U);
        source.ShardNames().Returns([new ShardName("Day", "All", 1U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing())
        {
            Database = new DatabaseUsage
            {
                Databases =
                [
                    new DatabaseDescriptor { ServerName = "host1", DatabaseName = "db1" },
                    new DatabaseDescriptor { ServerName = "host2", DatabaseName = "db2" }
                ]
            }
        };

        usage.Subscriptions.Add(new SubscriptionDescriptor(source, store));

        usage.PopulateAgentUris("event-subscriptions", identity);

        var subscription = usage.Subscriptions[0];
        subscription.AgentUris.Length.ShouldBe(2);
        subscription.AgentUris[0].ShouldBe("event-subscriptions://marten/main/host1.db1/day/all");
        subscription.AgentUris[1].ShouldBe("event-subscriptions://marten/main/host2.db2/day/all");
    }

    [Fact]
    public void inline_subscriptions_should_not_get_agent_uris()
    {
        var store = new FakeEventStore();
        var identity = new EventStoreIdentity("main", "marten");

        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.SingleStreamProjection);
        source.Name.Returns("Inline");
        source.Version.Returns(1U);
        source.ShardNames().Returns([new ShardName("Inline", "All", 1U)]);
        source.Lifecycle.Returns(ProjectionLifecycle.Inline);

        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing())
        {
            Database = new DatabaseUsage
            {
                MainDatabase = new DatabaseDescriptor
                {
                    ServerName = "localhost",
                    DatabaseName = "postgres"
                }
            }
        };

        usage.Subscriptions.Add(new SubscriptionDescriptor(source, store));

        usage.PopulateAgentUris("event-subscriptions", identity);

        var subscription = usage.Subscriptions[0];
        subscription.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void populate_agent_uris_with_multiple_shards()
    {
        var store = new FakeEventStore();
        var identity = new EventStoreIdentity("main", "marten");

        var source = Substitute.For<ISubscriptionSource>();
        source.Type.Returns(SubscriptionType.SingleStreamProjection);
        source.Name.Returns("Multi");
        source.Version.Returns(1U);
        source.ShardNames().Returns([
            new ShardName("Multi", "One", 1U),
            new ShardName("Multi", "Two", 1U)
        ]);
        source.Lifecycle.Returns(ProjectionLifecycle.Async);

        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing())
        {
            Database = new DatabaseUsage
            {
                MainDatabase = new DatabaseDescriptor
                {
                    ServerName = "localhost",
                    DatabaseName = "postgres"
                }
            }
        };

        usage.Subscriptions.Add(new SubscriptionDescriptor(source, store));

        usage.PopulateAgentUris("event-subscriptions", identity);

        var subscription = usage.Subscriptions[0];
        subscription.AgentUris.Length.ShouldBe(2);
        subscription.AgentUris[0].ShouldBe("event-subscriptions://marten/main/localhost.postgres/multi/one");
        subscription.AgentUris[1].ShouldBe("event-subscriptions://marten/main/localhost.postgres/multi/two");
    }
}

public class MyThing
{

}