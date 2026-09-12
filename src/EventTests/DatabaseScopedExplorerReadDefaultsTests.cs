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

namespace EventTests;

/// <summary>
/// jasperfx#810 — the explorer reads were scoped by tenant only, so on a store with more than one
/// database a store-global read answers from whichever database the default session resolved and looks
/// exactly like a complete answer. The database-scoped overloads add the missing dimension. These pin the
/// two halves of their default: on a single-database store the database argument is necessarily the
/// store's one database, so the read delegates; on a multi-database store the default refuses instead of
/// quietly answering from one of them.
/// </summary>
public class DatabaseScopedExplorerReadDefaultsTests
{
    // Overrides only the TENANT-scoped explorer reads, recording the tenant it was handed. That is what
    // lets these tests prove the database overload delegates with the tenant intact, rather than merely
    // failing to throw. The database-scoped overloads are the defaults under test.
    private sealed class ExplorerEventStore(DatabaseCardinality cardinality) : IEventStore
    {
        public List<string> Calls { get; } = [];

        public DatabaseCardinality DatabaseCardinality => cardinality;

        public Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, string? tenantId,
            CancellationToken ct)
        {
            Calls.Add($"GetRecentStreamsAsync({count}, {tenantId ?? "null"})");
            return Task.FromResult<IReadOnlyList<StreamSummary>>([]);
        }

        public async IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, string? tenantId,
            CancellationToken ct)
        {
            Calls.Add($"ReadStreamAsync({streamId}, {tenantId ?? "null"})");
            await Task.CompletedTask;
            yield break;
        }

        public Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, string? tenantId, CancellationToken ct)
        {
            Calls.Add($"GetStreamMetadataAsync({streamId}, {tenantId ?? "null"})");
            return Task.FromResult<StreamMetadata?>(null);
        }

        public async IAsyncEnumerable<EventRecord> QueryByTagsAsync(IReadOnlyDictionary<string, string> tags,
            string? tenantId, CancellationToken ct)
        {
            Calls.Add($"QueryByTagsAsync({tags.Count} tags, {tenantId ?? "null"})");
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<ProjectionStatus>> GetProjectionStatusesAsync(string? tenantId, CancellationToken ct)
        {
            Calls.Add($"GetProjectionStatusesAsync({tenantId ?? "null"})");
            return Task.FromResult<IReadOnlyList<ProjectionStatus>>([]);
        }

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
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    // Only the Identifier matters: the refusal has to name the database that could not be read, or an
    // operator staring at 512 shard databases learns nothing from it.
    private sealed class NamedEventDatabase(string identifier) : IEventDatabase
    {
        public string Identifier => identifier;
        public Uri DatabaseUri => throw new NotImplementedException();
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private readonly IEventDatabase theDatabase = new NamedEventDatabase("shard_017");
    private readonly Dictionary<string, string> theTags = new() { ["account"] = "abc" };

    private readonly ExplorerEventStore theRecorder = new(DatabaseCardinality.Single);

    // The database overloads are default interface members, so they are only reachable through the
    // interface — not off the concrete type. That is exactly how CritterWatch consumes them.
    private IEventStore theStore => theRecorder;

    #region single database delegates, tenant intact

    [Fact]
    public async Task get_recent_streams_on_a_single_database_store_delegates()
    {
        await theStore.GetRecentStreamsAsync(theDatabase, 5, "tenant-a", CancellationToken.None);
        theRecorder.Calls.ShouldBe(["GetRecentStreamsAsync(5, tenant-a)"]);
    }

    [Fact]
    public async Task read_stream_on_a_single_database_store_delegates()
    {
        await foreach (var _ in theStore.ReadStreamAsync(theDatabase, "stream-1", "tenant-a", CancellationToken.None))
        {
        }

        theRecorder.Calls.ShouldBe(["ReadStreamAsync(stream-1, tenant-a)"]);
    }

    [Fact]
    public async Task get_stream_metadata_on_a_single_database_store_delegates()
    {
        await theStore.GetStreamMetadataAsync(theDatabase, "stream-1", null, CancellationToken.None);
        theRecorder.Calls.ShouldBe(["GetStreamMetadataAsync(stream-1, null)"]);
    }

    [Fact]
    public async Task query_by_tags_on_a_single_database_store_delegates()
    {
        await foreach (var _ in theStore.QueryByTagsAsync(theDatabase, theTags, "tenant-a", CancellationToken.None))
        {
        }

        theRecorder.Calls.ShouldBe(["QueryByTagsAsync(1 tags, tenant-a)"]);
    }

    [Fact]
    public async Task projection_statuses_on_a_single_database_store_delegates()
    {
        await theStore.GetProjectionStatusesAsync(theDatabase, "tenant-a", CancellationToken.None);
        theRecorder.Calls.ShouldBe(["GetProjectionStatusesAsync(tenant-a)"]);
    }

    #endregion

    #region multiple databases refuse

    [Theory]
    [InlineData(DatabaseCardinality.StaticMultiple)]
    [InlineData(DatabaseCardinality.DynamicMultiple)]
    public async Task get_recent_streams_on_a_multi_database_store_refuses(DatabaseCardinality cardinality)
    {
        var recorder = new ExplorerEventStore(cardinality);
        IEventStore store = recorder;

        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => store.GetRecentStreamsAsync(theDatabase, 5, null, CancellationToken.None));

        // Naming the database is the whole point: the alternative behavior — quietly answering from one of
        // them — is indistinguishable from a complete answer, which is what jasperfx#810 is about.
        ex.Message.ShouldContain("shard_017");
        ex.Message.ShouldContain(cardinality.ToString());
        recorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void read_stream_on_a_multi_database_store_refuses()
    {
        // Expression-bodied rather than an async iterator, like the tenant overloads: a caller that never
        // enumerates still gets told the database scope was ignored.
        var recorder = new ExplorerEventStore(DatabaseCardinality.StaticMultiple);
        IEventStore store = recorder;

        Should.Throw<NotSupportedException>(
            () => store.ReadStreamAsync(theDatabase, "stream-1", null, CancellationToken.None));
        recorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task get_stream_metadata_on_a_multi_database_store_refuses()
    {
        var recorder = new ExplorerEventStore(DatabaseCardinality.DynamicMultiple);
        IEventStore store = recorder;

        await Should.ThrowAsync<NotSupportedException>(
            () => store.GetStreamMetadataAsync(theDatabase, "stream-1", null, CancellationToken.None));
        recorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void query_by_tags_on_a_multi_database_store_refuses()
    {
        var recorder = new ExplorerEventStore(DatabaseCardinality.StaticMultiple);
        IEventStore store = recorder;

        Should.Throw<NotSupportedException>(
            () => store.QueryByTagsAsync(theDatabase, theTags, null, CancellationToken.None));
        recorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task projection_statuses_on_a_multi_database_store_refuses()
    {
        var recorder = new ExplorerEventStore(DatabaseCardinality.DynamicMultiple);
        IEventStore store = recorder;

        await Should.ThrowAsync<NotSupportedException>(
            () => store.GetProjectionStatusesAsync(theDatabase, null, CancellationToken.None));
        recorder.Calls.ShouldBeEmpty();
    }

    #endregion

    [Fact]
    public void a_null_database_is_rejected_rather_than_silently_meaning_store_global()
    {
        // The single-database delegation would otherwise turn a null database into a store-global read,
        // which is precisely the ambiguity these overloads exist to remove.
        Should.Throw<ArgumentNullException>(
            () => theStore.GetRecentStreamsAsync(null!, 5, null, CancellationToken.None));
        Should.Throw<ArgumentNullException>(
            () => theStore.ReadStreamAsync(null!, "stream-1", null, CancellationToken.None));
        Should.Throw<ArgumentNullException>(
            () => theStore.GetStreamMetadataAsync(null!, "stream-1", null, CancellationToken.None));
        Should.Throw<ArgumentNullException>(
            () => theStore.QueryByTagsAsync(null!, theTags, null, CancellationToken.None));
        Should.Throw<ArgumentNullException>(
            () => theStore.GetProjectionStatusesAsync(null!, null, CancellationToken.None));

        theRecorder.Calls.ShouldBeEmpty();
    }
}
