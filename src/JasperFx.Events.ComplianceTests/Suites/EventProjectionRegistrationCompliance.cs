using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Document type written by an EventProjection but never registered with the store. See
/// https://github.com/JasperFx/marten/issues/4166.
/// </summary>
public class AuditRecord
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}

public class AuditableEvent
{
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// An EventProjection with an explicit <c>ApplyAsync</c> override that writes a document through
/// <c>operations.Store&lt;T&gt;()</c>. The source generator has to see that call and emit a
/// constructor registering <see cref="AuditRecord"/> as a published type — nothing else in the
/// configuration mentions it.
/// </summary>
/// <remarks>
/// The record id is the stream id rather than a fresh Guid so the end-to-end test can load it back.
/// </remarks>
public partial class AuditRecordProjection: ComplianceEventProjection
{
    public override ValueTask ApplyAsync(ComplianceOperations operations, IEvent e, CancellationToken cancellation)
    {
        switch (e.Data)
        {
            case AuditableEvent:
                // The explicit type argument is load-bearing: the generator's scan of ApplyAsync
                // bodies is syntactic, so it only sees Store<T>/Insert<T>/Update<T> written with one.
                operations.Store<AuditRecord>(new AuditRecord
                {
                    Id = e.StreamId,
                    StreamId = e.StreamId,
                    EventType = e.Data.GetType().Name,
                    Timestamp = e.Timestamp
                });
                break;
        }

        return new ValueTask();
    }
}

/// <summary>
/// The conventional route to the same registration: a <c>Create</c> method whose return type is the
/// published document type.
/// </summary>
public partial class AuditRecordCreatorProjection: ComplianceEventProjection
{
    public AuditRecord Create(AuditableEvent e) => new()
    {
        Id = Guid.NewGuid(), EventType = nameof(AuditableEvent)
    };
}

/// <summary>
/// A second document, because the reason <c>SubscriptionDescriptor.PublishedTypes</c> is a list is that an
/// event projection can write more than one (jasperfx#1102).
/// </summary>
public class AuditSummary
{
    public Guid Id { get; set; }
}

public class AuditSummarized;

public partial class AuditTrailProjection: ComplianceEventProjection
{
    public AuditSummary Create(AuditSummarized e) => new() { Id = Guid.NewGuid() };

    public AuditRecord Create(AuditableEvent e) => new()
    {
        Id = Guid.NewGuid(), EventType = nameof(AuditableEvent)
    };
}

/// <summary>
/// A self-aggregating document, so <see cref="EventProjectionRegistrationCompliance{TFixture,TOperations,TQuerySession}" />
/// has a single stream registration to find in the store's description of itself.
/// </summary>
public partial class AuditLedger
{
    public Guid Id { get; set; }
    public int Entries { get; set; }

    public static AuditLedger Create(AuditableEvent e) => new() { Entries = 1 };

    public void Apply(AuditSummarized e) => Entries++;
}

/// <summary>
/// Document types used by an EventProjection are discovered and registered without the user
/// declaring them — whether they show up as a <c>Store&lt;T&gt;</c> call inside an explicit
/// <c>ApplyAsync</c> override or as the return type of a conventional <c>Create</c> method. And a store
/// says so: whatever a projection publishes reaches <see cref="SubscriptionDescriptor.PublishedTypes" />.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor facts live here, rather than in <c>EventStoreExplorerCompliance</c> with the rest of
/// <see cref="IEventStore.TryCreateUsage" />, because this is the suite about what a projection publishes
/// and it is the one the in-memory store can run. The explorer's configuration needs an async registration.
/// </para>
/// <para>
/// A store that left the list empty would be indistinguishable from a store whose projections write
/// nothing, and every store wraps projection sources of its own, so each kind is asked for by name. Each
/// fact keeps the <c>usage == null</c> escape: a store that cannot describe itself is a legitimate answer.
/// </para>
/// <para>
/// A store's own composite wrapper for a bare <c>IProjection</c>, and its container-scoped registrations,
/// have no store-neutral way in through <see cref="ComplianceStoreConfig" />, so they are not reached here.
/// </para>
/// </remarks>
public abstract class EventProjectionRegistrationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_projection_registration";

        // Only the ApplyAsync projection is registered: the Create-convention one is asserted as a
        // bare object, and registering both would have two projections racing to write an
        // AuditRecord for the same event.
        config.AddProjection(new AuditRecordProjection(), ProjectionLifecycle.Inline);
    };

    /// <summary>
    /// One of each kind that runs inline, which is every kind a store without a daemon can hold. Separate
    /// from <see cref="_configuration" /> so the end-to-end fact keeps its one projection.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _describedConfiguration = config =>
    {
        config.SchemaName = "compliance_projection_description";

        config.Snapshot<AuditLedger>(SnapshotLifecycle.Inline);
        config.AddProjection(new ComplianceDepartmentProjection(), ProjectionLifecycle.Inline);
        config.AddProjection(new AuditTrailProjection(), ProjectionLifecycle.Inline);
    };

    private const string DescribedComposite = "ComplianceDescribedComposite";

    /// <summary>The fixture keys a rebuild on the delegate, so the delegate has to keep one instance.</summary>
    private static readonly ComplianceSubscription _subscription = new();

    /// <summary>
    /// Everything that needs a daemon, so a store without one skips it instead of refusing the registration.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _describedAsyncConfiguration = config =>
    {
        config.SchemaName = "compliance_projection_description_async";

        config.AddCompositeProjection(DescribedComposite, composite =>
        {
            composite.Snapshot<CompositeFirstStageTally>(1);
            composite.Snapshot<CompositeSecondStageTally>(2);
        });

        config.Subscribe(_subscription);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public void explicit_apply_async_projection_publishes_the_stored_document_type()
    {
        var projection = new AuditRecordProjection();

        projection.PublishedTypes().ShouldContain(typeof(AuditRecord),
            "AuditRecord should be discovered from the operations.Store<AuditRecord>() call in ApplyAsync");
    }

    [Fact]
    public void conventional_create_projection_publishes_its_return_type()
    {
        var projection = new AuditRecordCreatorProjection();

        projection.PublishedTypes().ShouldContain(typeof(AuditRecord),
            "AuditRecord should be discovered from the Create method's return type");
    }

    [Fact]
    public async Task unregistered_document_type_is_persisted_end_to_end()
    {
        // The assertion the registration checks above are a proxy for: if discovery worked, the
        // store has real storage for AuditRecord and the inline projection can write to it.
        await using var session = OpenSession();

        var streamId = Guid.NewGuid();
        EventsFor(session).StartStream(streamId, new AuditableEvent { Description = "created" });
        await SaveChangesAsync(session);

        var record = await LoadDocumentAsync<AuditRecord>(session, streamId);

        record.ShouldNotBeNull();
        record.StreamId.ShouldBe(streamId);
        record.EventType.ShouldBe(nameof(AuditableEvent));
    }

    // ---- what the store says about it (jasperfx#1102) ----

    private async Task<EventStoreUsage?> usageWithAsync(Action<ComplianceStoreConfig> configuration)
    {
        await theFixture.ConfigureAsync(configuration);

        return await EventStore.TryCreateUsage(Cancellation);
    }

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon, which composites and subscriptions require");
    }

    private static string[] namesOf(params Type[] types) => types.Select(x => TypeDescriptor.For(x).FullName).ToArray();

    private static string[] publishedBy(SubscriptionDescriptor? described)
    {
        described.ShouldNotBeNull("The store returned a usage descriptor that does not mention a registration.");

        return described.PublishedTypes.Select(x => x.FullName).ToArray();
    }

    private static SubscriptionDescriptor? theAggregationOf<TDoc>(EventStoreUsage usage)
    {
        return usage.Subscriptions.SingleOrDefault(x => x.AggregateType?.FullName == typeof(TDoc).FullName);
    }

    private static SubscriptionDescriptor? theProjection<TProjection>(EventStoreUsage usage)
    {
        return usage.Subscriptions.SingleOrDefault(x => x.ImplementationType?.FullName == typeof(TProjection).FullName);
    }

    [Fact]
    public async Task usage_reports_the_document_a_single_stream_projection_publishes()
    {
        var usage = await usageWithAsync(_describedConfiguration);

        if (usage == null)
        {
            return;
        }

        publishedBy(theAggregationOf<AuditLedger>(usage)).ShouldBe(namesOf(typeof(AuditLedger)));
    }

    [Fact]
    public async Task usage_reports_the_document_a_multi_stream_projection_publishes()
    {
        var usage = await usageWithAsync(_describedConfiguration);

        if (usage == null)
        {
            return;
        }

        publishedBy(theAggregationOf<ComplianceDepartment>(usage)).ShouldBe(namesOf(typeof(ComplianceDepartment)));
    }

    [Fact]
    public async Task usage_reports_every_document_an_event_projection_publishes_in_a_stable_order()
    {
        var usage = await usageWithAsync(_describedConfiguration);

        if (usage == null)
        {
            return;
        }

        // Declared the other way around on the projection, so source order cannot pass this
        publishedBy(theProjection<AuditTrailProjection>(usage)).ShouldBe(namesOf(typeof(AuditRecord), typeof(AuditSummary)));
    }

    [Fact]
    public async Task usage_reports_a_document_found_in_an_apply_async_body()
    {
        var usage = await usageWithAsync(_configuration);

        if (usage == null)
        {
            return;
        }

        publishedBy(theProjection<AuditRecordProjection>(usage)).ShouldBe(namesOf(typeof(AuditRecord)));
    }

    [Fact]
    public async Task usage_flattens_what_a_composite_publishes()
    {
        SkipUnlessDaemonIsSupported();

        var usage = await usageWithAsync(_describedAsyncConfiguration);

        if (usage == null)
        {
            return;
        }

        var described = usage.Subscriptions.SingleOrDefault(x => x.Name == DescribedComposite);

        publishedBy(described).ShouldBe(namesOf(typeof(CompositeFirstStageTally), typeof(CompositeSecondStageTally)));
    }

    [Fact]
    public async Task usage_reports_nothing_published_by_a_plain_subscription()
    {
        SkipUnlessDaemonIsSupported();

        var usage = await usageWithAsync(_describedAsyncConfiguration);

        if (usage == null)
        {
            return;
        }

        var described = usage.Subscriptions.SingleOrDefault(x => x.SubscriptionType == SubscriptionType.Subscription);

        publishedBy(described).ShouldBeEmpty();
    }
}
