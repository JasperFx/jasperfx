using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Aggregate stream stats events and aggregates

public record HarborCharted(string Name);

public record HarborDredged(int Depth);

public record FerryLaunched(string Name);

public record FerrySailed(int Miles);

/// <summary>On no aggregate's projection, so the streams that only hold these are untagged and stay that way.</summary>
public record TideLogged(int Height);

/// <summary>Registered as an inline snapshot, so it is one of the store's registered aggregate types.</summary>
public partial class ComplianceHarbor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Dredgings { get; set; }

    public static ComplianceHarbor Create(HarborCharted e) => new() { Name = e.Name };

    public void Apply(HarborDredged _) => Dredgings++;
}

/// <summary>Never registered: its streams are tagged with it only because they were started with it.</summary>
public partial class ComplianceFerry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Miles { get; set; }

    public static ComplianceFerry Create(FerryLaunched e) => new() { Name = e.Name };

    public void Apply(FerrySailed e) => Miles += e.Miles;
}

/// <summary>The string-keyed counterpart of <see cref="ComplianceFerry" />.</summary>
public partial class ComplianceKeyedFerry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Miles { get; set; }

    public static ComplianceKeyedFerry Create(FerryLaunched e) => new() { Name = e.Name };

    public void Apply(FerrySailed e) => Miles += e.Miles;
}

#endregion

/// <summary>
/// <see cref="IEventStore.RegisteredAggregateTypes" /> and <see cref="IEventDatabase.FetchAggregateStreamStatsAsync" />
/// (jasperfx#1282): the aggregate types a store knows, and its streams counted by aggregate type, which is what a
/// monitoring tool reports event-stream size by aggregate type from.
/// </summary>
/// <remarks>
/// What this does not cover: an alias no type in this deployment resolves to, which a portable test cannot
/// write; and per-tenant partitioned events, which no compliance configuration can switch on yet.
/// </remarks>
[RequiresCapability("SupportsAggregateStreamStats")]
public abstract class AggregateStreamStatsCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static void addEventTypes(ComplianceStoreConfig config)
    {
        config.AddEventType<HarborCharted>();
        config.AddEventType<HarborDredged>();
        config.AddEventType<FerryLaunched>();
        config.AddEventType<FerrySailed>();
        config.AddEventType<TideLogged>();
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_stream_stats";
        addEventTypes(config);
        config.Snapshot<ComplianceHarbor>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_stream_stats_conjoined";
        config.ConjoinedEventTenancy = true;
        addEventTypes(config);
    };

    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_stream_stats_string";
        config.StreamIdentity = StreamIdentity.AsString;
        addEventTypes(config);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void skipUnlessSupported()
        => Assert.SkipUnless(theFixture.SupportsAggregateStreamStats,
            "This event store does not implement RegisteredAggregateTypes and FetchAggregateStreamStatsAsync (jasperfx#1282).");

    private async Task<IReadOnlyList<AggregateStreamStats>> StatsAsync(AggregateStreamStatsRequest? request = null)
    {
        skipUnlessSupported();
        var database = (await EventStore.AllDatabases()).First();
        return await database.FetchAggregateStreamStatsAsync(request ?? new AggregateStreamStatsRequest(), Cancellation);
    }

    private string AliasFor<T>() => theFixture.Registry.AggregateAliasFor(typeof(T));

    private AggregateStreamStats StatsFor<T>(IReadOnlyList<AggregateStreamStats> stats)
    {
        var alias = AliasFor<T>();
        return stats.Where(x => x.AggregateTypeName == alias).ShouldHaveSingleItem(
            $"Expected one entry for {typeof(T).Name} under the alias '{alias}'");
    }

    private static object[] harborEvents(int count)
        => [new HarborCharted("Dol Amroth"), ..Enumerable.Range(1, count - 1).Select(i => (object)new HarborDredged(i))];

    private static object[] ferryEvents(int count)
        => [new FerryLaunched("Swan"), ..Enumerable.Range(1, count - 1).Select(i => (object)new FerrySailed(i))];

    private static object[] tideEvents(int count)
        => Enumerable.Range(1, count).Select(i => (object)new TideLogged(i)).ToArray();

    private async Task<TOperations> OpenAsync(string? tenantId)
    {
        if (tenantId == null) return OpenSession();

        var store = (IEventStore<TOperations, TQuerySession>)EventStore;
        return store.OpenSession((await EventStore.AllDatabases()).First(), tenantId);
    }

    private async Task<Guid> StartAsync<T>(object[] events, string? tenantId = null) where T : class
    {
        var id = Guid.NewGuid();
        await using var session = await OpenAsync(tenantId);
        EventsFor(session).StartStream<T>(id, events);
        await SaveChangesAsync(session);
        return id;
    }

    private async Task<Guid> StartUntaggedAsync(object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream(id, events);
        await SaveChangesAsync(session);
        return id;
    }

    [Fact]
    public void registered_aggregate_types_lists_a_registered_snapshot()
    {
        skipUnlessSupported();

        EventStore.RegisteredAggregateTypes().ShouldContain(typeof(ComplianceHarbor));
    }

    [Fact]
    public void registered_aggregate_types_agrees_with_the_projection_graph()
    {
        skipUnlessSupported();

        var registered = EventStore.RegisteredAggregateTypes();

        registered.ShouldBeUnique();
        registered.ShouldBe(theFixture.AllAggregateTypes().Distinct(), ignoreOrder: true);
    }

    [Fact]
    public async Task an_empty_database_has_no_stats()
    {
        (await StatsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task streams_are_counted_per_aggregate_type()
    {
        await StartAsync<ComplianceHarbor>(harborEvents(1));
        await StartAsync<ComplianceHarbor>(harborEvents(3));
        await StartAsync<ComplianceHarbor>(harborEvents(5));
        await StartAsync<ComplianceFerry>(ferryEvents(2));

        var stats = await StatsAsync();

        stats.Count.ShouldBe(2);

        var harbors = StatsFor<ComplianceHarbor>(stats);
        harbors.AggregateType.ShouldBe(typeof(ComplianceHarbor));
        harbors.StreamCount.ShouldBe(3);
        harbors.TotalEvents.ShouldBe(9);
        harbors.MaxVersion.ShouldBe(5);
        harbors.AverageVersion.ShouldBe(3);

        var ferries = StatsFor<ComplianceFerry>(stats);
        ferries.AggregateType.ShouldBe(typeof(ComplianceFerry));
        ferries.StreamCount.ShouldBe(1);
        ferries.TotalEvents.ShouldBe(2);
        ferries.MaxVersion.ShouldBe(2);
    }

    [Fact]
    public async Task untagged_streams_are_reported_under_no_alias()
    {
        await StartUntaggedAsync(tideEvents(4));
        await StartUntaggedAsync(tideEvents(2));
        await StartAsync<ComplianceFerry>(ferryEvents(1));

        var stats = await StatsAsync();

        var untagged = stats.Where(x => x.AggregateTypeName == null).ShouldHaveSingleItem();
        untagged.AggregateType.ShouldBeNull();
        untagged.StreamCount.ShouldBe(2);
        untagged.TotalEvents.ShouldBe(6);
        untagged.MaxVersion.ShouldBe(4);

        StatsFor<ComplianceFerry>(stats).StreamCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_stream_length_is_its_version_after_appends()
    {
        var id = await StartAsync<ComplianceFerry>(ferryEvents(2));

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(id, new FerrySailed(10), new FerrySailed(20), new FerrySailed(30));
            await SaveChangesAsync(session);
        }

        var ferries = StatsFor<ComplianceFerry>(await StatsAsync());
        ferries.TotalEvents.ShouldBe(5);
        ferries.MaxVersion.ShouldBe(5);
        ferries.LongestStreams.ShouldHaveSingleItem().Version.ShouldBe(5);
    }

    [Fact]
    public async Task length_buckets_count_the_streams_at_or_above_each_threshold()
    {
        await StartAsync<ComplianceHarbor>(harborEvents(1));
        await StartAsync<ComplianceHarbor>(harborEvents(3));
        await StartAsync<ComplianceHarbor>(harborEvents(5));

        // Out of order and duplicated on purpose: the buckets come back ascending, one per threshold
        var harbors = StatsFor<ComplianceHarbor>(await StatsAsync(new AggregateStreamStatsRequest
        {
            LengthThresholds = [5, 3, 100, 3, 1]
        }));

        harbors.LengthBuckets.ShouldBe([
            new StreamLengthBucket(1, 3),
            new StreamLengthBucket(3, 2),
            new StreamLengthBucket(5, 1),
            new StreamLengthBucket(100, 0)
        ]);
    }

    [Fact]
    public async Task no_thresholds_means_no_buckets()
    {
        await StartAsync<ComplianceHarbor>(harborEvents(2));

        StatsFor<ComplianceHarbor>(await StatsAsync(new AggregateStreamStatsRequest { LengthThresholds = [] }))
            .LengthBuckets.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_longest_streams_come_back_longest_first_per_type()
    {
        var short1 = await StartAsync<ComplianceHarbor>(harborEvents(1));
        var long1 = await StartAsync<ComplianceHarbor>(harborEvents(6));
        var middle = await StartAsync<ComplianceHarbor>(harborEvents(3));
        var ferry = await StartAsync<ComplianceFerry>(ferryEvents(9));

        var stats = await StatsAsync(new AggregateStreamStatsRequest { LongestStreams = 2 });

        var harbors = StatsFor<ComplianceHarbor>(stats);
        harbors.LongestStreams.Select(x => (x.Id, x.Version)).ShouldBe([(long1, 6L), (middle, 3L)]);
        harbors.LongestStreams.ShouldAllBe(x => x.Key == null);
        harbors.LongestStreams.ShouldNotContain(x => x.Id == short1);

        // The ferry is longer than every harbor, and still only ranks among ferries
        StatsFor<ComplianceFerry>(stats).LongestStreams.ShouldHaveSingleItem().Id.ShouldBe(ferry);
    }

    [Fact]
    public async Task asking_for_no_longest_streams_returns_none()
    {
        await StartAsync<ComplianceHarbor>(harborEvents(4));

        var harbors = StatsFor<ComplianceHarbor>(await StatsAsync(new AggregateStreamStatsRequest { LongestStreams = 0 }));

        harbors.LongestStreams.ShouldBeEmpty();
        harbors.StreamCount.ShouldBe(1);
    }

    [Fact]
    public async Task archived_streams_are_left_out_unless_asked_for()
    {
        skipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsStreamArchiving, "This event store does not archive streams.");

        await StartAsync<ComplianceHarbor>(harborEvents(2));
        var archived = await StartAsync<ComplianceHarbor>(harborEvents(7));

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(archived);
            await SaveChangesAsync(session);
        }

        var live = StatsFor<ComplianceHarbor>(await StatsAsync());
        live.StreamCount.ShouldBe(1);
        live.MaxVersion.ShouldBe(2);
        live.LongestStreams.ShouldNotContain(x => x.Id == archived);

        var all = StatsFor<ComplianceHarbor>(await StatsAsync(new AggregateStreamStatsRequest { IncludeArchived = true }));
        all.StreamCount.ShouldBe(2);
        all.MaxVersion.ShouldBe(7);
        all.LongestStreams[0].Id.ShouldBe(archived);
    }

    [Fact]
    public async Task a_tenant_counts_only_its_own_streams()
    {
        skipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy.");
        await theFixture.ConfigureAsync(_conjoinedConfiguration);
        await theFixture.CleanEventDataAsync();

        var acme = await StartAsync<ComplianceFerry>(ferryEvents(4), TenantA);
        await StartAsync<ComplianceFerry>(ferryEvents(2), TenantA);
        var globex = await StartAsync<ComplianceFerry>(ferryEvents(6), TenantB);

        var acmeFerries = StatsFor<ComplianceFerry>(await StatsAsync(new AggregateStreamStatsRequest { TenantId = TenantA }));
        acmeFerries.StreamCount.ShouldBe(2);
        acmeFerries.TotalEvents.ShouldBe(6);
        acmeFerries.LongestStreams[0].Id.ShouldBe(acme);
        acmeFerries.LongestStreams.ShouldAllBe(x => x.TenantId == TenantA);

        // No tenant: every tenant's streams, each longest stream carrying its own tenant
        var everyone = StatsFor<ComplianceFerry>(await StatsAsync());
        everyone.StreamCount.ShouldBe(3);
        everyone.TotalEvents.ShouldBe(12);
        everyone.LongestStreams[0].Id.ShouldBe(globex);
        everyone.LongestStreams[0].TenantId.ShouldBe(TenantB);
    }

    [Fact]
    public async Task string_keyed_streams_report_their_keys()
    {
        skipUnlessSupported();
        await theFixture.ConfigureAsync(_stringConfiguration);
        await theFixture.CleanEventDataAsync();

        var longKey = $"ferry/{Guid.NewGuid():N}-long";
        var shortKey = $"ferry/{Guid.NewGuid():N}-short";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceKeyedFerry>(longKey, ferryEvents(5));
            EventsFor(session).StartStream<ComplianceKeyedFerry>(shortKey, ferryEvents(2));
            await SaveChangesAsync(session);
        }

        var ferries = StatsFor<ComplianceKeyedFerry>(await StatsAsync());

        ferries.StreamCount.ShouldBe(2);
        ferries.LongestStreams.Select(x => (x.Key, x.Version)).ShouldBe([(longKey, 5L), (shortKey, 2L)]);
        ferries.LongestStreams.ShouldAllBe(x => x.Id == Guid.Empty);
    }
}
