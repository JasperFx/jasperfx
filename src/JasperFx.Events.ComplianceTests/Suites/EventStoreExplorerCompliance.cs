using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Explorer events

public record VoyageBegun(string Ship);

public record PortVisited(string Port);

#endregion

/// <summary>
/// An aggregate registered as an <see cref="ProjectionLifecycle.Inline"/> snapshot, so the suite has
/// a projection registration to look for in <c>EventStoreUsage.Subscriptions</c>.
/// </summary>
/// <remarks>
/// Inline rather than async on purpose: an implementation that described the daemon's shards instead
/// of the registrations would look correct and still answer nothing for an inline-only store, which
/// is exactly the shape of the store that went several releases describing none of its projections
/// (JasperFx/fisher#120). <c>SubscriptionDescriptor</c> already handles Inline — its metrics block is
/// gated on <see cref="ProjectionLifecycle.Async"/> — so requiring it costs nothing.
/// </remarks>
public partial class VoyageSnapshot
{
    public Guid Id { get; set; }
    public string Ship { get; set; } = string.Empty;
    public List<string> Ports { get; set; } = new();

    public static VoyageSnapshot Create(VoyageBegun e) => new() { Ship = e.Ship };

    public void Apply(PortVisited e) => Ports.Add(e.Port);
}

/// <summary>
/// A second aggregate, registered as an <see cref="ProjectionLifecycle.Async"/> snapshot, so the
/// suite covers the half of <c>EventStoreUsage.Subscriptions</c> that <see cref="VoyageSnapshot"/>
/// cannot.
/// </summary>
/// <remarks>
/// <c>projections rebuild</c> resolves a target by name and then by shard, so an Async registration
/// reporting no <c>ShardNames</c> is a projection the command can find and cannot run. Inline is
/// covered deliberately by <see cref="VoyageSnapshot"/> (see its remarks); this is the other side of
/// the same argument, and neither substitutes for the other.
/// </remarks>
public partial class VoyageLog
{
    public Guid Id { get; set; }
    public int Ports { get; set; }

    public static VoyageLog Create(VoyageBegun e) => new();

    public void Apply(PortVisited e) => Ports++;
}

/// <summary>
/// The event store explorer surface on <see cref="IEventStore"/> — <c>GetRecentStreamsAsync</c>,
/// <c>GetStreamMetadataAsync</c> and <c>TryCreateUsage</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are default-interface methods that throw unless a store implements them, which makes them
/// unusually easy to leave half-built: nothing fails at compile time, and each product has its own
/// tests. They also have an out-of-repo consumer — this is the surface CritterWatch reads — so a
/// cross-store contract test is the cheapest guard against a tooling regression on one store only.
/// </para>
/// <para>
/// Per-tenant overloads are deliberately not exercised here; they belong with the conjoined tenancy
/// suite, which owns the tenant-scoped session seam.
/// </para>
/// </remarks>
public abstract class EventStoreExplorerCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_explorer";
        config.AddEventType<VoyageBegun>();
        config.AddEventType<PortVisited>();
        config.Snapshot<VoyageSnapshot>(SnapshotLifecycle.Inline);
        config.Snapshot<VoyageLog>(SnapshotLifecycle.Async);

        // Asymmetric on purpose: every metadata assertion below is that the descriptor reports what
        // was CONFIGURED. Turning both on, or leaving both off, would pass against a descriptor that
        // hardcoded the answer.
        config.EnableCorrelationTracking = true;
        config.EnableHeaders = false;

        config.MaxConcurrentRebuildsPerDatabase = RebuildCap;
    };

    /// <summary>
    /// A value nothing derives, so a descriptor reporting it cannot have guessed. Deliberately not a
    /// round number for the same reason.
    /// </summary>
    private const int RebuildCap = 3;

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private bool SupportsExplorer => theFixture.SupportsExplorerSurface;

    private async Task<Guid> aVoyageAsync(params object[] extra)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new VoyageBegun("Beagle") }.Concat(extra).ToArray();
        EventsFor(session).StartStream(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task recent_streams_reports_the_streams_that_exist()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var first = await aVoyageAsync();
        var second = await aVoyageAsync(new PortVisited("Plymouth"));

        var streams = await EventStore.GetRecentStreamsAsync(10, Cancellation);

        var ids = streams.Select(x => x.StreamId).ToList();
        ids.ShouldContain(first.ToString());
        ids.ShouldContain(second.ToString());

        var summary = streams.Single(x => x.StreamId == second.ToString());
        summary.Version.ShouldBe(2);
        summary.CreatedAt.ShouldNotBe(default);
        summary.LastUpdatedAt.ShouldNotBe(default);
        summary.LastUpdatedAt.ShouldBeGreaterThanOrEqualTo(summary.CreatedAt);
    }

    [Fact]
    public async Task recent_streams_honours_the_count_and_orders_newest_first()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        await aVoyageAsync();
        await Task.Delay(20, Cancellation);
        await aVoyageAsync();
        await Task.Delay(20, Cancellation);
        var newest = await aVoyageAsync();

        var streams = await EventStore.GetRecentStreamsAsync(2, Cancellation);

        streams.Count.ShouldBe(2);
        streams[0].StreamId.ShouldBe(newest.ToString());
        streams[0].LastUpdatedAt.ShouldBeGreaterThanOrEqualTo(streams[1].LastUpdatedAt);
    }

    [Fact]
    public async Task asking_for_more_streams_than_exist_is_not_an_error()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        await aVoyageAsync();

        var streams = await EventStore.GetRecentStreamsAsync(1000, Cancellation);

        streams.Count.ShouldBeGreaterThanOrEqualTo(1);
        streams.Count.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task stream_metadata_for_a_known_stream()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var streamId = await aVoyageAsync(new PortVisited("Plymouth"), new PortVisited("Bahia"));

        var metadata = await EventStore.GetStreamMetadataAsync(streamId.ToString(), Cancellation);

        metadata.ShouldNotBeNull();
        metadata.StreamId.ShouldBe(streamId.ToString());
        metadata.Version.ShouldBe(3);
        metadata.IsArchived.ShouldBeFalse();
        metadata.CreatedAt.ShouldNotBe(default);

        // Non-nullable in the record's declaration: "no tags" is an empty dictionary, not null.
        // Polecat returned null here until polecat#412; this assertion is what found it.
        metadata.Tags.ShouldNotBeNull();
    }

    [Fact]
    public async Task stream_metadata_for_an_unknown_stream_is_null()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var metadata = await EventStore.GetStreamMetadataAsync(Guid.NewGuid().ToString(), Cancellation);

        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task usage_describes_the_registered_event_types()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        // TryCreateUsage is allowed to return null for a store that cannot describe itself, but a
        // store that returns one must know the event types it was configured with.
        if (usage == null)
        {
            return;
        }

        var names = usage.Events.Select(x => x.EventTypeName).ToList();
        names.ShouldContain(EventTypeNameFor<VoyageBegun>());
        names.ShouldContain(EventTypeNameFor<PortVisited>());
    }

    [Fact]
    public async Task usage_describes_the_registered_projections()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        // Same escape as above: a store that cannot describe itself is a legitimate answer. What is
        // being caught here is a store that DOES return a usage and silently under-fills it.
        if (usage == null)
        {
            return;
        }

        // Every member of EventStoreUsage is a list or a nullable that starts empty, so an unfilled
        // slot is indistinguishable from a genuinely empty one — no exception, no warning, and no
        // field saying which of the two it is. Consumers then render a confident wrong answer:
        // "projections list" prints "No projections in this store" for a store with twenty of them,
        // "projections rebuild" matches none of them, and CritterWatch sees a store with no
        // projections. That is JasperFx/fisher#120, where the fix was one missing Describe() call.
        usage.Subscriptions.ShouldNotBeEmpty(
            "The store returned a usage descriptor but left Subscriptions empty, even though a projection was registered. Two shipped commands read this slot directly.");

        usage.Subscriptions.Select(x => x.Name).ShouldContain(nameof(VoyageSnapshot));
    }

    // ---- the rest of the descriptor (JasperFx/fisher#712) ----
    //
    // Everything below shares one argument, so it is stated once here rather than in each test.
    //
    // Every member of EventStoreUsage is a list or a nullable that starts empty, so a store that
    // never fills one is INDISTINGUISHABLE from a store that genuinely has none of that thing. There
    // is no exception, no warning, and no field saying which of the two it is -- so a consumer
    // renders a confident, wrong answer. That is how fisher#120 happened, and jasperfx#700 above
    // closed exactly one slot. These close the rest.
    //
    // Each keeps the `usage == null` escape: a store that cannot describe itself at all remains a
    // legitimate answer. What is being caught is a store that DOES return a descriptor and silently
    // under-fills it.

    /// <summary>
    /// <see cref="EventStoreUsage"/> carries the event registry twice and a consumer may read either.
    /// </summary>
    /// <remarks>
    /// Filling one and not the other is JasperFx/polecat#411, where the unfilled list read as "this
    /// store has no event types configured". <c>usage_describes_the_registered_event_types</c> above
    /// asserts on <c>Events</c> only, so that exact bug was invisible to this suite even after it had
    /// happened once.
    /// </remarks>
    [Fact]
    public async Task usage_fills_both_event_type_collections()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        usage.RegisteredEventTypes.ShouldNotBeEmpty(
            "The store returned a usage descriptor and filled Events but left RegisteredEventTypes empty. A consumer may read either.");

        var aliases = usage.RegisteredEventTypes.Select(x => x.Alias).ToList();
        aliases.ShouldContain(EventTypeNameFor<VoyageBegun>());
        aliases.ShouldContain(EventTypeNameFor<PortVisited>());
    }

    /// <summary>
    /// An <see cref="ProjectionLifecycle.Async"/> registration is described with the shards it runs as.
    /// </summary>
    /// <remarks>
    /// <c>projections rebuild</c> resolves its target by name and then by shard, so an async
    /// projection described without shard names is one the command can list and cannot run.
    /// </remarks>
    [Fact]
    public async Task usage_describes_an_async_projection_with_its_shards()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        var described = usage.Subscriptions.SingleOrDefault(x => x.Name == nameof(VoyageLog));

        described.ShouldNotBeNull(
            "The store returned a usage descriptor that does not mention an Async projection registration.");
        described.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        described.ShardNames.ShouldNotBeEmpty(
            "An Async projection was described with no shard names, so 'projections rebuild' can find it and cannot run it.");
    }

    /// <summary>
    /// The lifecycle a projection was registered with survives onto the descriptor.
    /// </summary>
    /// <remarks>
    /// Asserted across both registrations at once rather than on either alone, because a descriptor
    /// that hardcoded one lifecycle would satisfy a single-registration test whichever value it
    /// picked.
    /// </remarks>
    [Fact]
    public async Task usage_reports_the_lifecycle_each_projection_was_registered_with()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        var byName = usage.Subscriptions.ToDictionary(x => x.Name);

        byName.ShouldContainKey(nameof(VoyageSnapshot));
        byName.ShouldContainKey(nameof(VoyageLog));

        byName[nameof(VoyageSnapshot)].Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
        byName[nameof(VoyageLog)].Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }

    /// <summary>
    /// The two projection error policies are both described, and are not the same object read twice.
    /// </summary>
    /// <remarks>
    /// They differ deliberately: a rebuild stops on an error a continuous run skips. A console reading
    /// one for the other offers "view related dead letters" for a store that halts instead -- a button
    /// that never returns anything. Reached through
    /// <c>IEventStore&lt;TOperations, TQuerySession&gt;</c>, the same cast
    /// <see cref="DeadLetterCompliance{TFixture,TOperations,TQuerySession}"/> uses.
    /// </remarks>
    [Fact]
    public async Task usage_describes_both_projection_error_policies_separately()
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

        store.ContinuousErrors.SkipApplyErrors = true;
        store.RebuildErrors.SkipApplyErrors = false;

        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        usage.ProjectionErrors.ShouldNotBeNull(
            "The store returned a usage descriptor with no continuous-run projection error policy.");
        usage.ProjectionRebuildErrors.ShouldNotBeNull(
            "The store returned a usage descriptor with no rebuild projection error policy.");

        // Opposed values, so a descriptor reporting one policy for both fails here rather than
        // agreeing by coincidence.
        usage.ProjectionErrors.SkipApplyErrors.ShouldBeTrue();
        usage.ProjectionRebuildErrors.SkipApplyErrors.ShouldBeFalse();
    }

    /// <summary>
    /// The opt-in event metadata flags report what was configured rather than a default.
    /// </summary>
    /// <remarks>
    /// JasperFx/jasperfx#475. A query facet built over a metadata column that is switched off filters
    /// on a column the table does not have. The suite turns correlation tracking on and leaves headers
    /// off, so a descriptor hardcoding either answer fails on one of them.
    /// </remarks>
    [Fact]
    public async Task usage_reports_the_opt_in_metadata_flags_as_configured()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        usage.EventMetadata.ShouldNotBeNull(
            "The store returned a usage descriptor with no event metadata capabilities.");

        usage.EventMetadata.StoreType.ShouldNotBeNullOrEmpty();

        usage.EventMetadata.CorrelationId.ShouldBeTrue();
        usage.EventMetadata.Headers.ShouldBeFalse();
    }

    /// <summary>
    /// The rebuild concurrency cap round-trips onto the descriptor.
    /// </summary>
    /// <remarks>
    /// The value is on <see cref="ComplianceStoreConfig"/>, on <see cref="IEventStore"/> and on
    /// <see cref="EventStoreUsage"/>, so a set-and-read-back test is available with no seam. Asserted
    /// against the configured value rather than merely non-null, because the default is derived and a
    /// descriptor reporting the default would look filled.
    /// </remarks>
    [Fact]
    public async Task usage_reports_the_configured_rebuild_concurrency_cap()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        usage.MaxConcurrentRebuildsPerDatabase.ShouldBe(RebuildCap);
    }

    /// <summary>
    /// The physical maximum event sequence is reported once events exist.
    /// </summary>
    /// <remarks>
    /// The gap between this and the high-water mark is what a monitoring console renders as projection
    /// lag, so leaving it null renders as "n/a" and says nothing. Deliberately asserted as "present and
    /// at least as many events as were appended" rather than as an exact value: how far a store's
    /// sequence runs ahead of the events it has issued is the store's business, and on a store whose
    /// committed sequences are contiguous it equals the high-water mark exactly.
    /// </remarks>
    [Fact]
    public async Task usage_reports_the_max_event_sequence_once_events_exist()
    {
        await aVoyageAsync(new PortVisited("Plymouth"));

        var usage = await EventStore.TryCreateUsage(Cancellation);

        if (usage == null)
        {
            return;
        }

        usage.MaxEventSequence.ShouldNotBeNull(
            "The store returned a usage descriptor with no MaxEventSequence, which renders as 'n/a' in a console.");
        usage.MaxEventSequence.Value.ShouldBeGreaterThanOrEqualTo(2);
    }
}
