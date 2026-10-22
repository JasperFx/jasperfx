using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#1031 — the store-agnostic preview and advance of a high water mark. Both ship as default
/// interface members so out-of-tree stores keep compiling, and the defaults refuse rather than answer:
/// an operator action that silently did nothing would read as done. <c>ProjectionHost</c>, whose
/// <c>AdvanceHighWaterMarkToLatestAsync</c> threw <see cref="NotImplementedException" />, now runs over it.
/// </summary>
public class HighWaterAdvanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IEventDatabase database(string identifier)
    {
        var db = Substitute.For<IEventDatabase>();
        db.Identifier.Returns(identifier);
        return db;
    }

    [Fact]
    public async Task the_default_preview_refuses()
    {
        IEventStore store = new DefaultsOnly();

        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => store.PreviewHighWaterAdvanceAsync(database("main"), null, Token));
        ex.Message.ShouldContain("jasperfx#1031");
    }

    [Fact]
    public async Task the_default_advance_refuses()
    {
        IEventStore store = new DefaultsOnly();

        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => store.AdvanceHighWaterMarkToLatestAsync(database("main"), null, 5, Token));
        ex.Message.ShouldContain("jasperfx#1031");
    }

    [Theory]
    [InlineData(10, 20, true)]
    [InlineData(20, 20, false)]
    [InlineData(25, 20, false)]
    public void a_preview_would_advance_only_below_the_ceiling(long current, long ceiling, bool expected)
    {
        new HighWaterMarkPreview("main", null, current, ceiling, 0).WouldAdvance.ShouldBe(expected);
    }

    [Fact]
    public async Task the_projection_host_advances_every_selected_database_with_a_null_tenant()
    {
        var one = database("one");
        var two = database("two");
        var three = database("three");
        var store = new AdvancingEventStore(one, two, three);

        var selection = new ProjectionSelection(new EventStoreUsage());
        selection.DatabaseIdentifiers.AddRange(["one", "THREE"]);

        var results = await ProjectionHost.AdvanceHighWaterMarksAsync(store, selection, Token);

        store.Calls.ShouldBe([("one", null, null), ("three", null, null)]);
        results.Select(x => x.DatabaseIdentifier).ShouldBe(["one", "three"]);
    }

    [Fact]
    public async Task the_projection_host_refuses_a_database_the_store_does_not_list()
    {
        // AllDatabases() defaults to an empty list, so a store that has not implemented it would otherwise
        // turn an operator's advance into a silent no-op.
        var store = new AdvancingEventStore(database("one"));

        var selection = new ProjectionSelection(new EventStoreUsage());
        selection.DatabaseIdentifiers.AddRange(["one", "missing"]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => ProjectionHost.AdvanceHighWaterMarksAsync(store, selection, Token));
        ex.Message.ShouldContain("'missing'");
        store.Calls.ShouldBeEmpty();
    }

    private sealed class AdvancingEventStore(params IEventDatabase[] databases) : DefaultsOnly, IEventStore
    {
        public List<(string Database, string? TenantId, long? Expected)> Calls { get; } = new();

        public ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases()
            => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>(databases);

        public Task<IReadOnlyList<HighWaterMarkAdvance>> AdvanceHighWaterMarkToLatestAsync(
            IEventDatabase database, string? tenantId, long? expectedCurrentMark, CancellationToken token)
        {
            Calls.Add((database.Identifier, tenantId, expectedCurrentMark));
            return Task.FromResult<IReadOnlyList<HighWaterMarkAdvance>>(
            [
                new HighWaterMarkAdvance(database.Identifier, tenantId, HighWaterAdvanceOutcome.Advanced, 3, 7, 1)
            ]);
        }
    }

    // Implements nothing beyond the abstract members, so every default interface member is the shipped one.
    private class DefaultsOnly : IEventStore
    {
        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => new("test://store");

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
}
