using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace EventTests;

/// <summary>
/// jasperfx#1282: both members ship as default interface members so out-of-tree stores keep compiling. An empty
/// answer would read as "no aggregates" or "no streams", so the defaults throw, naming the store.
/// </summary>
public class AggregateStreamStatsDefaultsTests
{
    private sealed class BareEventDatabase : IEventDatabase
    {
        public string Identifier => "bare";
        public Uri DatabaseUri => new("test://bare");
        public string StorageIdentifier => "bare";
        public ShardStateTracker Tracker => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private sealed class BareEventStore : IEventStore
    {
        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task the_database_default_throws_rather_than_reporting_no_streams()
    {
        IEventDatabase database = new BareEventDatabase();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            database.FetchAggregateStreamStatsAsync(new AggregateStreamStatsRequest(), CancellationToken.None));

        ex.Message.ShouldContain("jasperfx#1282");
        ex.Message.ShouldContain(nameof(BareEventDatabase));
    }

    [Fact]
    public void the_store_default_throws_rather_than_reporting_no_aggregates()
    {
        IEventStore store = new BareEventStore();

        var ex = Should.Throw<NotSupportedException>(() => store.RegisteredAggregateTypes());

        ex.Message.ShouldContain("jasperfx#1282");
        ex.Message.ShouldContain(nameof(BareEventStore));
    }

    [Fact]
    public void the_default_request()
    {
        var request = new AggregateStreamStatsRequest();

        request.TenantId.ShouldBeNull();
        request.IncludeArchived.ShouldBeFalse();
        request.LengthThresholds.ShouldBe([100L, 1_000L, 10_000L]);
        request.LongestStreams.ShouldBe(10);
    }

    [Fact]
    public void thresholds_are_kept_ascending_and_distinct()
    {
        new AggregateStreamStatsRequest { LengthThresholds = [1_000, 5, 1_000, 50] }
            .LengthThresholds.ShouldBe([5L, 50L, 1_000L]);
    }

    [Fact]
    public void no_thresholds_is_allowed()
    {
        new AggregateStreamStatsRequest { LengthThresholds = [] }.LengthThresholds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void a_threshold_must_be_positive(long threshold)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AggregateStreamStatsRequest { LengthThresholds = [10, threshold] });
    }

    [Fact]
    public void the_longest_stream_count_may_be_zero_but_not_negative()
    {
        new AggregateStreamStatsRequest { LongestStreams = 0 }.LongestStreams.ShouldBe(0);

        Should.Throw<ArgumentOutOfRangeException>(() => new AggregateStreamStatsRequest { LongestStreams = -1 });
    }

    [Fact]
    public void a_request_copied_with_with_keeps_its_normalized_thresholds()
    {
        var request = new AggregateStreamStatsRequest { LengthThresholds = [20, 10] } with { TenantId = "acme" };

        request.LengthThresholds.ShouldBe([10L, 20L]);
        request.TenantId.ShouldBe("acme");
    }

    [Fact]
    public void the_average_version_of_no_streams_is_zero()
    {
        new AggregateStreamStats(null, null, 0, 0, 0, [], []).AverageVersion.ShouldBe(0);
        new AggregateStreamStats("harbor", null, 4, 10, 6, [], []).AverageVersion.ShouldBe(2.5);
    }
}
