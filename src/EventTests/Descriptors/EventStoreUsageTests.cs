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

    [Fact]
    public void max_event_sequence_defaults_to_null()
    {
        // CW#150 signal 2 semantics: null means "not populated by this
        // implementation" — CritterWatch renders that as "n/a" rather than 0.
        new EventStoreUsage().MaxEventSequence.ShouldBeNull();
        new EventStoreUsage(new Uri("marten://main"), new MyThing()).MaxEventSequence.ShouldBeNull();
    }

    [Fact]
    public void max_event_sequence_round_trips()
    {
        var usage = new EventStoreUsage
        {
            MaxEventSequence = 123_456L
        };

        usage.MaxEventSequence.ShouldBe(123_456L);
    }

    [Fact]
    public void projection_error_handling_descriptors_default_to_null()
    {
        // JasperFx/ProductSupport#3: null means "not populated by this
        // implementation" so CritterWatch can fall back to a policy-agnostic
        // copy rather than imply skip-and-DLQ behavior on older monitored
        // services.
        new EventStoreUsage().ProjectionErrors.ShouldBeNull();
        new EventStoreUsage().ProjectionRebuildErrors.ShouldBeNull();
        new EventStoreUsage(new Uri("marten://main"), new MyThing()).ProjectionErrors.ShouldBeNull();
        new EventStoreUsage(new Uri("marten://main"), new MyThing()).ProjectionRebuildErrors.ShouldBeNull();
    }

    [Fact]
    public void projection_error_handling_descriptors_round_trip()
    {
        var usage = new EventStoreUsage
        {
            // Normal-run: JasperFx.Events 2.0 default — skip-and-DLQ on Apply,
            // skip serialization errors, stop on unknown events.
            ProjectionErrors = new ProjectionErrorHandlingDescriptor
            {
                SkipApplyErrors = true,
                SkipUnknownEvents = false,
                SkipSerializationErrors = true
            },
            // Rebuild-mode: tighter — every class of error stops the rebuild,
            // matching the JasperFx.Events 2.0 RebuildErrors defaults.
            ProjectionRebuildErrors = new ProjectionErrorHandlingDescriptor
            {
                SkipApplyErrors = false,
                SkipUnknownEvents = false,
                SkipSerializationErrors = false
            }
        };

        usage.ProjectionErrors.ShouldNotBeNull();
        usage.ProjectionErrors.SkipApplyErrors.ShouldBeTrue();
        usage.ProjectionErrors.SkipUnknownEvents.ShouldBeFalse();
        usage.ProjectionErrors.SkipSerializationErrors.ShouldBeTrue();

        usage.ProjectionRebuildErrors.ShouldNotBeNull();
        usage.ProjectionRebuildErrors.SkipApplyErrors.ShouldBeFalse();
        usage.ProjectionRebuildErrors.SkipUnknownEvents.ShouldBeFalse();
        usage.ProjectionRebuildErrors.SkipSerializationErrors.ShouldBeFalse();
    }
}

public class MyThing
{

}