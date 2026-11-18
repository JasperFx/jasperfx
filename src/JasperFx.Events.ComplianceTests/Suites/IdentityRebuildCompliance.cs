using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Identity rebuild fixtures

public record CounterTicked;

public record CounterPoisoned;

public record CounterClosed;

public record CounterNoted(string Note);

/// <summary>
/// The counting half of <see cref="IdentityRebuildCounterProjection" />: every event the projection handles adds
/// one and records its sequence, so a lost event and a doubled one both show as a wrong number and a wrong list.
/// </summary>
public partial class ComplianceWatchtower
{
    public int Ticks { get; set; }
    public List<long> TickSequences { get; set; } = new();
}

/// <summary>
/// A single-stream projection for <see cref="IdentityRebuildCompliance{TFixture,TOperations,TQuerySession}" />.
/// </summary>
/// <remarks>
/// It reuses the <c>ComplianceWatchtowerProjectionBase</c> alias every consumer already declares, so this suite
/// needs no new alias. The state it keeps for the facts is static and keyed by stream, because the projection
/// instance is built by the suite's configuration and every store runs its facts in parallel with other suites.
/// </remarks>
public partial class IdentityRebuildCounterProjection: ComplianceWatchtowerProjectionBase
{
    public const string ProjectionName = "IdentityRebuildCounter";

    /// <summary>Streams whose <see cref="CounterPoisoned" /> event throws when applied.</summary>
    public static readonly ConcurrentDictionary<Guid, bool> Poisoned = new();

    /// <summary>How often <c>RaiseSideEffects</c> ran for each stream.</summary>
    public static readonly ConcurrentDictionary<Guid, int> SideEffectCalls = new();

    public IdentityRebuildCounterProjection()
    {
        Name = ProjectionName;
    }

    public void Apply(IEvent<CounterTicked> @event, ComplianceWatchtower tower)
    {
        tower.Ticks++;
        tower.TickSequences.Add(@event.Sequence);
    }

    public void Apply(IEvent<CounterPoisoned> @event, ComplianceWatchtower tower)
    {
        if (Poisoned.ContainsKey(@event.StreamId))
        {
            throw new InvalidOperationException($"Stream {@event.StreamId} is poisoned");
        }

        tower.Ticks++;
        tower.TickSequences.Add(@event.Sequence);
    }

    public bool ShouldDelete(CounterClosed _) => true;

    public override ValueTask RaiseSideEffects(ComplianceOperations operations,
        IEventSlice<ComplianceWatchtower> slice)
    {
        var stream = slice.Events().FirstOrDefault()?.StreamId;
        if (stream.HasValue)
        {
            SideEffectCalls.AddOrUpdate(stream.Value, 1, (_, count) => count + 1);
        }

        return new ValueTask();
    }
}

#endregion

/// <summary>
/// Rebuilding one identity of a single-stream projection inside the shard that runs it (jasperfx#1201): the
/// re-fold is bound to the next batch the shard builds and commits with its progression.
/// </summary>
/// <remarks>
/// <para>
/// The counting projection is the point. A document that "looks right" proves nothing about a rebuild racing
/// live appends; a counter that equals its stream's event count, and a sequence list that equals its stream's
/// sequences, proves nothing was lost and nothing was applied twice.
/// </para>
/// <para>
/// JasperFx's own tests pin the orderings this suite cannot time on a real database: a request posted while one
/// page commits and others are in flight, two requests for the same identity in one batch, and the long-stream
/// pre-fold with an append between it and the bound batch.
/// </para>
/// </remarks>
[RequiresCapability("SupportsIdentityRebuild")]
public abstract class IdentityRebuildCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static void configureCore(ComplianceStoreConfig config, Action<IdentityRebuildCounterProjection>? alter)
    {
        config.SchemaName = "compliance_identity_rebuild";

        config.AddEventType<CounterTicked>();
        config.AddEventType<CounterPoisoned>();
        config.AddEventType<CounterClosed>();
        config.AddEventType<CounterNoted>();

        var projection = new IdentityRebuildCounterProjection();
        alter?.Invoke(projection);
        config.AddProjection(projection, ProjectionLifecycle.Async);
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config => configureCore(config, null);

    private static readonly Action<ComplianceStoreConfig> _cachedConfiguration =
        config => configureCore(config, x => x.Options.CacheLimitPerTenant = 1000);

    private static readonly Action<ComplianceStoreConfig> _softDeletedConfiguration = config =>
    {
        configureCore(config, null);
        config.SoftDeleted<ComplianceWatchtower>();
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        configureCore(config, null);

        // jasperfx#1227: a schema of its own, like every other suite's conjoined configuration, so this fact never
        // migrates the single-tenancy event store the other facts built
        config.SchemaName = "compliance_identity_rebuild_conjoined";
        config.ConjoinedEventTenancy = true;
    };

    private static readonly Action<ComplianceStoreConfig> _withMultiStreamConfiguration = config =>
    {
        configureCore(config, null);

        config.AddEventType<EmployeeHired>();
        config.AddEventType<EmployeeDeparted>();
        config.AddEventType<DepartmentsAudited>();
        config.AddEventType<TeamOnboarded>();
        config.AddProjection(new ComplianceDepartmentProjection(), ProjectionLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsIdentityRebuild,
            "This event store does not implement the per-identity rebuild seams (jasperfx#1201)");
    }

    private async Task reconfigureAsync(Action<ComplianceStoreConfig> configuration)
    {
        await theFixture.ConfigureAsync(configuration);
        await theFixture.CleanEventDataAsync();
    }

    private IEventStore<TOperations, TQuerySession> Store => (IEventStore<TOperations, TQuerySession>)EventStore;

    private async Task<IEventDatabase> theDatabaseAsync() => (await EventStore.AllDatabases()).First();

    private ShardName theShard => EventStore.RegisteredShardNames()
        .Single(x => x.Name == IdentityRebuildCounterProjection.ProjectionName);

    private async Task<Guid> aCounterAsync(params object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceWatchtower>(id, events);
        await SaveChangesAsync(session);
        return id;
    }

    private async Task appendAsync(Guid id, params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(id, events);
        await SaveChangesAsync(session);
    }

    private static object[] ticks(int count) => Enumerable.Range(0, count).Select(_ => (object)new CounterTicked()).ToArray();

    private async Task<ComplianceWatchtower?> loadAsync(Guid id)
    {
        await using var session = OpenSession();
        return await LoadDocumentAsync<ComplianceWatchtower>(session, id);
    }

    // jasperfx#1227: a soft-deleted document loads as null on most stores, and comes back flagged deleted on a store
    // whose load by id does not apply the soft-delete filter (Marten)
    private async Task shouldBeSoftDeletedAsync(Guid id)
    {
        var document = await loadAsync(id);
        if (theFixture.LoadExcludesSoftDeletedDocuments)
        {
            document.ShouldBeNull();
            return;
        }

        document.ShouldNotBeNull();
        await using var session = OpenSession();
        (await theFixture.IsSoftDeletedAsync<ComplianceWatchtower>(session, id, Cancellation)).ShouldBeTrue();
    }

    private async Task<ComplianceWatchtower> shouldBeLiveAsync(Guid id)
    {
        var document = (await loadAsync(id)).ShouldNotBeNull();
        if (!theFixture.LoadExcludesSoftDeletedDocuments)
        {
            await using var session = OpenSession();
            (await theFixture.IsSoftDeletedAsync<ComplianceWatchtower>(session, id, Cancellation)).ShouldBeFalse();
        }

        return document;
    }

    private async Task<IReadOnlyList<IEvent>> streamAsync(Guid id)
    {
        await using var session = OpenSession();
        return await EventsFor(session).FetchStreamAsync(id, token: Cancellation);
    }

    private async Task corruptAsync(Guid id)
    {
        await using var session = OpenSession();
        StoreDocument(session, new ComplianceWatchtower { Id = id, Ticks = 999, TickSequences = [-1] });
        await SaveChangesAsync(session);
    }

    // What the projection would have written from the whole stream: one per handled event, in sequence order
    private async Task theDocumentMatchesItsStreamAsync(Guid id)
    {
        var expected = (await streamAsync(id))
            .Where(x => x.Data is CounterTicked or CounterPoisoned)
            .Select(x => x.Sequence)
            .ToList();

        var document = await loadAsync(id);
        document.ShouldNotBeNull($"stream {id} has no document");
        document.TickSequences.ShouldBe(expected, $"stream {id}: lost or doubled events");
        document.Ticks.ShouldBe(expected.Count);
    }

    private Task<IdentityRebuildResult> rebuildAsync(IProjectionDaemon daemon, Guid id,
        string? tenantId = null)
        => daemon.RebuildIdentityAsync(IdentityRebuildCounterProjection.ProjectionName, tenantId, id, Cancellation);

    private async Task<IProjectionDaemon> aCaughtUpDaemonAsync()
    {
        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        return daemon;
    }

    // Skip the events in (current, sequence] without projecting them, the way an event that commits inside a
    // skipped high-water range is never seen by the shard
    private async Task skipShardPastAsync(long sequence)
    {
        await Store.RewindAgentProgressAsync(await theDatabaseAsync(), theShard.Identity, Cancellation, sequence);
    }

    [Fact]
    public async Task a_corrupted_document_is_restored_and_no_other_document_changes()
    {
        SkipUnlessSupported();

        var corrupted = await aCounterAsync(ticks(3));
        var bystander = await aCounterAsync(ticks(2));

        var daemon = await aCaughtUpDaemonAsync();
        var before = await loadAsync(bystander);

        await corruptAsync(corrupted);

        var result = await rebuildAsync(daemon, corrupted);

        result.Action.ShouldBe(ActionType.Store);
        await theDocumentMatchesItsStreamAsync(corrupted);

        var after = await loadAsync(bystander);
        after.ShouldNotBeNull();
        after.Ticks.ShouldBe(before!.Ticks);
        after.TickSequences.ShouldBe(before.TickSequences);
    }

    private async Task rebuildRepeatedlyWhileAppendingAsync(Guid rebuilt, Guid other)
    {
        var daemon = await aCaughtUpDaemonAsync();

        using var appending = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var appender = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await appendAsync(rebuilt, new CounterTicked());
                await appendAsync(other, new CounterTicked(), new CounterTicked());
            }
        }, appending.Token);

        // Corrupted each time, so a page that went on applying its own events for the identity on top of the stored
        // document, instead of leaving them to the re-fold, would leave the corruption behind
        for (var i = 0; i < 8; i++)
        {
            await corruptAsync(rebuilt);
            await rebuildAsync(daemon, rebuilt);
        }

        await appender;
        await WaitForNonStaleProjectionDataAsync(_timeout);
    }

    [Fact]
    public async Task no_event_is_applied_twice_while_rebuilding_during_appends()
    {
        SkipUnlessSupported();

        var rebuilt = await aCounterAsync(ticks(5));
        var other = await aCounterAsync(ticks(5));

        await rebuildRepeatedlyWhileAppendingAsync(rebuilt, other);

        var document = await loadAsync(rebuilt);
        document.ShouldNotBeNull();
        document.TickSequences.Distinct().Count().ShouldBe(document.TickSequences.Count);
        document.Ticks.ShouldBe((await streamAsync(rebuilt)).Count);
    }

    [Fact]
    public async Task no_event_is_lost_while_rebuilding_during_appends()
    {
        SkipUnlessSupported();

        var rebuilt = await aCounterAsync(ticks(5));
        var other = await aCounterAsync(ticks(5));

        await rebuildRepeatedlyWhileAppendingAsync(rebuilt, other);

        await theDocumentMatchesItsStreamAsync(rebuilt);
        await theDocumentMatchesItsStreamAsync(other);
    }

    [Fact]
    public async Task an_idle_caught_up_shard_completes_a_request_without_moving_its_progression()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(3));
        var daemon = await aCaughtUpDaemonAsync();
        var database = await theDatabaseAsync();

        var position = await database.ProjectionProgressFor(theShard, Cancellation);
        position.ShouldBeGreaterThan(0);

        await corruptAsync(id);
        var result = await rebuildAsync(daemon, id);

        result.BoundCeiling.ShouldBe(position);
        (await database.ProjectionProgressFor(theShard, Cancellation)).ShouldBe(position);
        await theDocumentMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task a_stream_that_folds_to_nothing_ends_with_no_document()
    {
        SkipUnlessSupported();

        var deleted = await aCounterAsync(new CounterTicked(), new CounterTicked(), new CounterClosed());
        var unhandled = await aCounterAsync(new CounterNoted("nothing the projection reads"));

        var daemon = await aCaughtUpDaemonAsync();
        (await loadAsync(deleted)).ShouldBeNull();

        await corruptAsync(deleted);
        await corruptAsync(unhandled);

        (await rebuildAsync(daemon, deleted)).Action.ShouldBe(ActionType.Delete);
        (await rebuildAsync(daemon, unhandled)).Action.ShouldBe(ActionType.Nothing);

        (await loadAsync(deleted)).ShouldBeNull();
        (await loadAsync(unhandled)).ShouldBeNull();
    }

    [Fact]
    public async Task a_poison_event_faults_the_request_and_the_shard_keeps_running()
    {
        SkipUnlessSupported();

        var poisoned = await aCounterAsync(new CounterTicked(), new CounterPoisoned(), new CounterTicked());
        var other = await aCounterAsync(ticks(2));

        var daemon = await aCaughtUpDaemonAsync();
        await corruptAsync(poisoned);

        IdentityRebuildCounterProjection.Poisoned[poisoned] = true;
        try
        {
            await Should.ThrowAsync<Exception>(() => rebuildAsync(daemon, poisoned));

            daemon.StatusFor(theShard.Identity).ShouldBe(AgentStatus.Running);

            await appendAsync(other, new CounterTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);
            await theDocumentMatchesItsStreamAsync(other);

            // Left exactly as it was
            var document = await loadAsync(poisoned);
            document.ShouldNotBeNull();
            document.Ticks.ShouldBe(999);
        }
        finally
        {
            IdentityRebuildCounterProjection.Poisoned.TryRemove(poisoned, out _);
        }
    }

    [Fact]
    public async Task the_same_batch_callback_commits_with_the_rebuild()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(3));
        var daemon = await aCaughtUpDaemonAsync();
        await corruptAsync(id);

        var marker = Guid.NewGuid();
        var request = new IdentityRebuildRequest(StorageConstants.DefaultTenantId, id)
        {
            InSameBatch = batch =>
            {
                var session = ((IProjectionBatch<TOperations, TQuerySession>)batch)
                    .SessionForTenant(StorageConstants.DefaultTenantId);
                theFixture.StoreDocument(session, new ComplianceWatchtower { Id = marker, Ticks = -7 });
                return new ValueTask();
            }
        };

        await daemon.RebuildIdentityAsync(IdentityRebuildCounterProjection.ProjectionName, request, Cancellation);

        await theDocumentMatchesItsStreamAsync(id);
        (await loadAsync(marker)).ShouldNotBeNull().Ticks.ShouldBe(-7);
    }

    [Fact]
    public async Task neither_the_rebuild_nor_the_callback_persists_when_the_batch_fails()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(3));
        var daemon = await aCaughtUpDaemonAsync();
        await corruptAsync(id);

        var database = await theDatabaseAsync();
        var marker = Guid.NewGuid();
        var request = new IdentityRebuildRequest(StorageConstants.DefaultTenantId, id)
        {
            InSameBatch = async batch =>
            {
                var session = ((IProjectionBatch<TOperations, TQuerySession>)batch)
                    .SessionForTenant(StorageConstants.DefaultTenantId);
                theFixture.StoreDocument(session, new ComplianceWatchtower { Id = marker, Ticks = -7 });

                // Move the progression out from under the batch, so its floor-checked update fails the commit
                await Store.RewindAgentProgressAsync(database, theShard.Identity, Cancellation, 1_000_000);
            }
        };

        await Should.ThrowAsync<Exception>(() =>
            daemon.RebuildIdentityAsync(IdentityRebuildCounterProjection.ProjectionName, request, Cancellation));

        (await loadAsync(marker)).ShouldBeNull();
        (await loadAsync(id)).ShouldNotBeNull().Ticks.ShouldBe(999);
    }

    // Leaves the shard having skipped one event of the stream, as a late event inside a skipped high-water range
    // would: the document is missing it until something re-folds the stream
    private async Task<(IProjectionDaemon Daemon, Guid Id)> aShardThatMissedAnEventAsync()
    {
        var id = await aCounterAsync(ticks(3));
        await aCaughtUpDaemonAsync();
        await StopDaemonAsync();

        await appendAsync(id, new CounterTicked());
        await skipShardPastAsync((await streamAsync(id)).Last().Sequence);

        var daemon = await StartDaemonAsync();

        // Seen by the shard on top of the document that misses the skipped event
        await appendAsync(id, new CounterTicked());
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await loadAsync(id))!.Ticks.ShouldBe(4, "the shard should have missed the skipped event");

        return (daemon, id);
    }

    [Fact]
    public async Task with_the_aggregate_cache_on_the_cache_takes_the_rebuilt_aggregate()
    {
        SkipUnlessSupported();
        await reconfigureAsync(_cachedConfiguration);

        var (daemon, id) = await aShardThatMissedAnEventAsync();

        await rebuildAsync(daemon, id);
        await theDocumentMatchesItsStreamAsync(id);

        // Applied on top of the cached aggregate: a stale cache would drop the event the rebuild restored
        await appendAsync(id, new CounterTicked());
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await theDocumentMatchesItsStreamAsync(id);
    }

    [Fact]
    public async Task rebuilding_an_identity_for_one_tenant_leaves_another_tenants_untouched()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy");
        await reconfigureAsync(_conjoinedConfiguration);

        var id = Guid.NewGuid();
        foreach (var tenant in new[] { "red", "blue" })
        {
            await using var session = Store.OpenSession(await theDatabaseAsync(), tenant);
            EventsFor(session).StartStream<ComplianceWatchtower>(id, ticks(tenant == "red" ? 3 : 2));
            await SaveChangesAsync(session);
        }

        var daemon = await aCaughtUpDaemonAsync();

        async Task<ComplianceWatchtower?> loadForAsync(string tenant)
        {
            await using var session = Store.OpenSession(await theDatabaseAsync(), tenant);
            return await LoadDocumentAsync<ComplianceWatchtower>(session, id);
        }

        foreach (var tenant in new[] { "red", "blue" })
        {
            await using var session = Store.OpenSession(await theDatabaseAsync(), tenant);
            StoreDocument(session, new ComplianceWatchtower { Id = id, Ticks = 999 });
            await SaveChangesAsync(session);
        }

        await rebuildAsync(daemon, id, "red");

        (await loadForAsync("red")).ShouldNotBeNull().Ticks.ShouldBe(3);
        (await loadForAsync("blue")).ShouldNotBeNull().Ticks.ShouldBe(999);
    }

    [Fact]
    public async Task a_late_event_below_the_shard_position_is_included_after_a_rebuild()
    {
        SkipUnlessSupported();

        var (daemon, id) = await aShardThatMissedAnEventAsync();

        var result = await rebuildAsync(daemon, id);

        result.Action.ShouldBe(ActionType.Store);
        await theDocumentMatchesItsStreamAsync(id);
        (await loadAsync(id))!.Ticks.ShouldBe(5);
    }

    [Fact]
    public async Task side_effects_do_not_fire_during_the_refold()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(3));
        var daemon = await aCaughtUpDaemonAsync();

        // They do fire in normal running, or the assertion below would pass vacuously
        IdentityRebuildCounterProjection.SideEffectCalls.TryGetValue(id, out var before);
        before.ShouldBeGreaterThan(0);

        await corruptAsync(id);
        await rebuildAsync(daemon, id);

        IdentityRebuildCounterProjection.SideEffectCalls[id].ShouldBe(before);
    }

    [Fact]
    public async Task a_refold_that_recreates_a_soft_deleted_document_leaves_it_not_deleted()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsSoftDeletedProjectionDocuments,
            "This event store's fixture cannot store a projected document soft-deleted");
        await reconfigureAsync(_softDeletedConfiguration);

        // Projected up to the soft delete
        var id = await aCounterAsync(new CounterTicked(), new CounterTicked(), new CounterClosed());
        await aCaughtUpDaemonAsync();
        await shouldBeSoftDeletedAsync(id);

        // A tick the shard never sees means the stream now folds to a live document that storage has deleted
        await StopDaemonAsync();
        await appendAsync(id, new CounterTicked());
        await skipShardPastAsync((await streamAsync(id)).Last().Sequence);
        var daemon = await StartDaemonAsync();

        // The shard must have committed once since the restart for its position to be known
        (await rebuildAsync(daemon, id)).Action.ShouldBe(ActionType.Store);

        var document = await shouldBeLiveAsync(id);
        document.Ticks.ShouldBe(1);
    }

    [Fact]
    public async Task a_refold_that_soft_deletes_still_soft_deletes()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsSoftDeletedProjectionDocuments,
            "This event store's fixture cannot store a projected document soft-deleted");
        await reconfigureAsync(_softDeletedConfiguration);

        var id = await aCounterAsync(new CounterTicked(), new CounterTicked(), new CounterClosed());
        var daemon = await aCaughtUpDaemonAsync();

        await corruptAsync(id);

        // Through the projection's own delete, not a hard delete
        (await rebuildAsync(daemon, id)).Action.ShouldBe(ActionType.Delete);
        await shouldBeSoftDeletedAsync(id);
    }

    [Fact]
    public async Task a_multi_stream_projection_is_refused()
    {
        SkipUnlessSupported();
        await reconfigureAsync(_withMultiStreamConfiguration);

        await aCounterAsync(ticks(1));
        var daemon = await aCaughtUpDaemonAsync();

        // Named after the projection on some stores and after its document on others
        var multiStream = EventStore.RegisteredShardNames()
            .Select(x => x.Name)
            .First(x => x != IdentityRebuildCounterProjection.ProjectionName);

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            daemon.RebuildIdentityAsync(multiStream, null, "engineering", Cancellation));

        refused.Reason.ShouldBe(IdentityRebuildRefusal.NotASingleStreamProjection);
    }

    [Fact]
    public async Task a_shard_that_has_never_committed_is_refused()
    {
        SkipUnlessSupported();

        var daemon = await StartDaemonAsync();

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            rebuildAsync(daemon, Guid.NewGuid()));

        refused.Reason.ShouldBe(IdentityRebuildRefusal.ShardHasNeverCommitted);
    }

    [Fact]
    public async Task a_shard_not_running_here_is_refused()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(1));
        var daemon = await aCaughtUpDaemonAsync();
        await daemon.StopAgentAsync(theShard);

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() => rebuildAsync(daemon, id));

        refused.Reason.ShouldBeOneOf(IdentityRebuildRefusal.ShardNotRunningHere, IdentityRebuildRefusal.ShardStopped);
    }

    [Fact]
    public async Task a_shard_in_rebuild_mode_is_refused()
    {
        SkipUnlessSupported();

        var id = await aCounterAsync(ticks(1));
        var daemon = await aCaughtUpDaemonAsync();
        await daemon.StopAgentAsync(theShard);
        await daemon.StartAgentInRebuildModeAsync(theShard, Cancellation);

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() => rebuildAsync(daemon, id));

        refused.Reason.ShouldBe(IdentityRebuildRefusal.ShardIsRebuilding);
    }
}
