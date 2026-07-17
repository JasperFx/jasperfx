using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#435 — ReadProjectionProgressAsync ships as a default interface implementation so the
/// out-of-tree IEventDatabase implementors (Marten, Polecat) keep compiling on the 1.x line. These
/// pin the contract that matters to a consumer: "not implemented" and "no row yet" are different
/// answers, and the default must not conflate them.
/// </summary>
public class ReadProjectionProgressDefaultsTests
{
    // A bare IEventDatabase that does NOT override ReadProjectionProgressAsync, so the call below
    // exercises the default. Everything else throws — those members are never invoked here.
    private sealed class BareEventDatabase : IEventDatabase
    {
        public string Identifier => throw new NotImplementedException();
        public Uri DatabaseUri => throw new NotImplementedException();
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
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

    // An IEventDatabase that DOES implement the query, standing in for Marten/Polecat. Proves the
    // member is overridable and that a real implementation's null survives as "no row".
    private sealed class ProgressionEventDatabase : BareDatabaseBase
    {
        public override ValueTask<ProjectionProgressRow?> ReadProjectionProgressAsync(
            string projectionName, string? tenantId, CancellationToken token)
            => projectionName == "Orders"
                ? new ValueTask<ProjectionProgressRow?>(
                    new ProjectionProgressRow("Orders", tenantId, 42, "Running", null))
                : new ValueTask<ProjectionProgressRow?>((ProjectionProgressRow?)null);
    }

    // Split out so the overriding stub does not have to restate every unrelated member.
    private abstract class BareDatabaseBase : IEventDatabase
    {
        public virtual ValueTask<ProjectionProgressRow?> ReadProjectionProgressAsync(
            string projectionName, string? tenantId, CancellationToken token)
            => throw new NotImplementedException();

        public string Identifier => throw new NotImplementedException();
        public Uri DatabaseUri => throw new NotImplementedException();
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
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

    private readonly IEventDatabase theBareDatabase = new BareEventDatabase();
    private readonly IEventDatabase theProgressionDatabase = new ProgressionEventDatabase();

    [Fact]
    public async Task default_throws_rather_than_reporting_a_live_cell_as_absent()
    {
        // The critical distinction: null is the documented "no row for this pair yet" answer, so an
        // unimplemented store must not return it. A monitoring UI would render a running projection
        // as having no progress at all.
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await theBareDatabase.ReadProjectionProgressAsync("Orders", null, CancellationToken.None));
    }

    [Fact]
    public async Task default_throws_for_a_tenant_scoped_read_too()
    {
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await theBareDatabase.ReadProjectionProgressAsync("Orders", "tenant-1", CancellationToken.None));
    }

    [Fact]
    public async Task an_implementing_store_can_return_a_row()
    {
        var row = await theProgressionDatabase.ReadProjectionProgressAsync("Orders", "tenant-1", CancellationToken.None);

        row.ShouldNotBeNull();
        row.ProjectionName.ShouldBe("Orders");
        row.TenantId.ShouldBe("tenant-1");
        row.Sequence.ShouldBe(42);
        row.AgentStatus.ShouldBe("Running");
        row.LastHeartbeat.ShouldBeNull();
    }

    [Fact]
    public async Task an_implementing_store_returns_null_when_no_row_exists_for_the_pair()
    {
        var row = await theProgressionDatabase.ReadProjectionProgressAsync("Unobserved", null, CancellationToken.None);
        row.ShouldBeNull();
    }

    [Fact]
    public void null_tenant_id_means_store_global_or_default_tenant()
    {
        new ProjectionProgressRow("Orders", null, 1, "Running", null).TenantId.ShouldBeNull();
    }

    // jasperfx#435: agent state is only persisted where a store both models the column and writes it.
    // Neither Marten nor Polecat writes agent_status today, so a store must be able to report "I have
    // no agent state for this cell" rather than invent one.
    [Fact]
    public void null_agent_status_means_the_store_does_not_persist_one()
    {
        new ProjectionProgressRow("Orders", null, 1, null, null).AgentStatus.ShouldBeNull();
    }
}
