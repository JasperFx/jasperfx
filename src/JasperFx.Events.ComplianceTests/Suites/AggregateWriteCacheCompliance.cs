using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Fetching;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Aggregate write cache events and aggregates

public record LedgerStarted(string Owner);

public record AmountPosted(decimal Amount);

public record AmountReversed(decimal Amount);

/// <summary>
/// Inline-snapshotted and enrolled in the cache. The aggregate is deliberately mutable, which is the
/// shape the take-on-read requirement exists for.
/// </summary>
public partial class CachedLedger
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static CachedLedger Create(LedgerStarted e) => new() { Owner = e.Owner };

    public void Apply(AmountPosted e) => Balance += e.Amount;

    public void Apply(AmountReversed e) => Balance -= e.Amount;
}

/// <summary>
/// Identical to <see cref="CachedLedger" /> and deliberately <em>not</em> enrolled. Exists so the
/// suite can assert that caching is off unless asked for, rather than store-wide.
/// </summary>
public partial class UncachedLedger
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static UncachedLedger Create(LedgerStarted e) => new() { Owner = e.Owner };

    public void Apply(AmountPosted e) => Balance += e.Amount;

    public void Apply(AmountReversed e) => Balance -= e.Amount;
}

/// <summary>
/// Async-snapshotted and enrolled. The daemon is never started in this suite, which is deliberate:
/// it means the stored snapshot always lags the stream, so every fetch has real delta events to fold
/// onto whatever baseline it starts from. That is the harder case for a cache and it needs no
/// timing.
/// </summary>
public partial class CachedJournal
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static CachedJournal Create(LedgerStarted e) => new() { Owner = e.Owner };

    public void Apply(AmountPosted e) => Balance += e.Amount;

    public void Apply(AmountReversed e) => Balance -= e.Amount;
}

#endregion

/// <summary>
/// The second-level aggregate snapshot cache behind <c>FetchForWriting</c> — jasperfx#674.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: a store that has not wired <see cref="IAggregateWriteCache" /> into its fetch plans does
/// not enroll, and never reaches
/// <see cref="IComplianceStoreRegistrar.CacheAggregatesForWriting{TDoc}" />.
/// </para>
/// <para>
/// <b>What the contract actually is.</b> The cache supplies a <em>baseline</em> and nothing more.
/// The stream version and every event after the cached version are still read from the database on
/// every call, and the concurrency assertion on append is untouched. So the suite's job is to prove
/// that turning caching on is <em>unobservable</em> except in latency — that a fetch off a hit is
/// indistinguishable from a fetch off a miss, including when the cached baseline is wrong.
/// </para>
/// <para>
/// ⚠️ Every one of those facts is vacuously true of a store that ignored the opt-in altogether, and
/// that is the failure mode most likely to ship. <see cref="RecordingAggregateWriteCache.Hits" /> is
/// what separates the two, and
/// <see cref="the_cache_is_actually_consulted_when_a_type_opts_in" /> is the fact holding it.
/// </para>
/// <para>
/// ⛔ <b>Note what is deliberately absent.</b> There is no fact that a cache hit skips the version
/// read, because it must not: a "trusted" cache that appended blind at the cached version was
/// measured at 0.19 ms of a 13.2 ms round and is retired. If a future implementation makes
/// <see cref="a_cached_baseline_cannot_suppress_a_concurrency_exception" /> fail, that is the
/// feature being reintroduced, not a test needing a gate.
/// </para>
/// <para>
/// <b>When an entry is written is not pinned.</b> The two lifecycles differ — an Async snapshot can
/// be cached at fetch time, while an Inline snapshot is mutated by the projection during commit and
/// so can only be cached once the commit succeeds. Both are correct, and the difference is
/// observable: a fetch that appends nothing may never warm the cache at all. Every arrange step here
/// therefore fetches, appends and commits, so it warms the cache under either policy.
/// </para>
/// </remarks>
public abstract class AggregateWriteCacheCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly RecordingAggregateWriteCache _cache = new();

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_aggregate_cache";
        config.Snapshot<CachedLedger>(SnapshotLifecycle.Inline);
        config.Snapshot<UncachedLedger>(SnapshotLifecycle.Inline);
        config.Snapshot<CachedJournal>(SnapshotLifecycle.Async);

        // One shared cache instance across all three types, so the "not enrolled" assertion is about
        // the store's opt-in check rather than about which cache it happened to be handed.
        config.CacheAggregatesForWriting<CachedLedger>(_cache);
        config.CacheAggregatesForWriting<CachedJournal>(_cache);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    protected RecordingAggregateWriteCache TheCache => _cache;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _cache.Reset();
    }

    private async Task<Guid> anOpenVaultAsync<T>(params object[] events) where T : class
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var all = new List<object> { new LedgerStarted("Hilda") };
        all.AddRange(events);
        EventsFor(session).StartStream<T>(streamId, all);
        await SaveChangesAsync(session);

        return streamId;
    }

    /// <summary>
    /// Fetch, append and commit — the round that leaves an entry behind under either write-back
    /// policy. See the note on the class.
    /// </summary>
    private async Task warmAsync<T>(Guid streamId, decimal amount) where T : class
    {
        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<T>(streamId, Cancellation);
        stream.AppendOne(new AmountPosted(amount));
        await SaveChangesAsync(session);
    }

    private async Task appendFromAnotherSessionAsync<T>(Guid streamId, decimal amount) where T : class
    {
        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<T>(streamId, Cancellation);
        stream.AppendOne(new AmountPosted(amount));
        await SaveChangesAsync(session);
    }

    #region The cache is real

    /// <remarks>
    /// The load-bearing fact of the whole suite. Without it, a store that dropped the opt-in on the
    /// floor passes everything below.
    /// </remarks>
    [Fact]
    public async Task the_cache_is_actually_consulted_when_a_type_opts_in()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));

        await warmAsync<CachedLedger>(streamId, 10);

        TheCache.WasStoredFor(typeof(CachedLedger)).ShouldBeTrue();

        var hitsBefore = TheCache.Hits;
        await warmAsync<CachedLedger>(streamId, 1);

        TheCache.Hits.ShouldBeGreaterThan(hitsBefore);
    }

    /// <remarks>
    /// Caching is per aggregate type, not store-wide. A store enabling it globally the moment any
    /// type opts in fails here.
    /// </remarks>
    [Fact]
    public async Task a_type_that_did_not_opt_in_is_never_cached()
    {
        var streamId = await anOpenVaultAsync<UncachedLedger>(new AmountPosted(100));

        await warmAsync<UncachedLedger>(streamId, 10);
        await warmAsync<UncachedLedger>(streamId, 5);

        TheCache.WasStoredFor(typeof(UncachedLedger)).ShouldBeFalse();
        TheCache.TakenKeys.ShouldNotContain(x => x.DocumentType == typeof(UncachedLedger));
    }

    /// <remarks>
    /// The key carries the store's own database identifier and tenant resolution — neither of which
    /// the suite could construct — and the aggregate identity. All three have to be filled in or two
    /// databases, two tenants or two streams collide.
    /// </remarks>
    [Fact]
    public async Task the_stored_key_identifies_the_aggregate_it_came_from()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        var key = TheCache.LastKeyFor(typeof(CachedLedger));

        key.ShouldNotBeNull();
        key.Value.DocumentType.ShouldBe(typeof(CachedLedger));
        key.Value.Id.ShouldBe(streamId);
        key.Value.DatabaseIdentifier.ShouldNotBeNullOrWhiteSpace();
        key.Value.TenantId.ShouldNotBeNullOrWhiteSpace();
    }

    /// <remarks>
    /// Two streams of the same type must not share an entry. Trivially true of any correct key, and
    /// cheap insurance against a store keying on the document type alone.
    /// </remarks>
    [Fact]
    public async Task two_streams_of_the_same_type_get_their_own_entries()
    {
        var first = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        var second = await anOpenVaultAsync<CachedLedger>(new AmountPosted(7));

        await warmAsync<CachedLedger>(first, 10);
        await warmAsync<CachedLedger>(second, 1);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedLedger>(second, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(8);
    }

    #endregion

    #region A hit is indistinguishable from a miss

    [Fact]
    public async Task a_cache_hit_returns_the_state_a_cold_fetch_would_have()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedLedger>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Owner.ShouldBe("Hilda");
        stream.Aggregate.Balance.ShouldBe(110);
        stream.CurrentVersion.ShouldBe(3);
        stream.StartingVersion.ShouldBe(3);
    }

    /// <remarks>
    /// The one that matters most for correctness. A cached baseline is only a starting point — the
    /// events another writer committed after it still have to be read and folded on. A store
    /// returning the cached instance as-is passes every other fact here and silently loses writes.
    /// </remarks>
    [Fact]
    public async Task a_cached_baseline_still_sees_events_appended_by_someone_else()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        await appendFromAnotherSessionAsync<CachedLedger>(streamId, 25);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedLedger>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(135);
        stream.CurrentVersion.ShouldBe(4);
    }

    /// <remarks>
    /// A baseline planted ahead of the stream is what a database restore or a rollback produces. The
    /// store cannot fold negative deltas onto it, so it has to notice and redo the fetch uncached
    /// rather than hand back state the database does not have.
    /// </remarks>
    [Fact]
    public async Task a_cached_baseline_ahead_of_the_stream_heals_itself()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        var key = TheCache.LastKeyFor(typeof(CachedLedger));
        key.ShouldNotBeNull();

        TheCache.Seed(key.Value, new CachedLedger { Owner = "Hilda", Balance = 9999 }, 500);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedLedger>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(110);
        stream.CurrentVersion.ShouldBe(3);
    }

    /// <remarks>
    /// Dropping an entry is always sound — it is the escape hatch a store or an operator has, and
    /// the reason <see cref="IAggregateWriteCache" /> lets an implementation evict whenever it
    /// likes. The next fetch simply takes the uncached path.
    /// </remarks>
    [Fact]
    public async Task an_evicted_entry_falls_back_to_a_correct_uncached_fetch()
    {
        var streamId = await anOpenVaultAsync<CachedJournal>(new AmountPosted(100));
        await warmAsync<CachedJournal>(streamId, 10);

        var key = TheCache.LastKeyFor(typeof(CachedJournal));
        key.ShouldNotBeNull();

        TheCache.Evict(key.Value);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedJournal>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(110);
        stream.CurrentVersion.ShouldBe(3);
    }

    #endregion

    #region Optimistic concurrency is untouched

    /// <remarks>
    /// <para>
    /// The fact the issue asked for by name: a cached baseline can never suppress a concurrency
    /// exception that an uncached fetch would have raised.
    /// </para>
    /// <para>
    /// Both writers fetch off a warm cache, then one commits and the other must still fail. Compare
    /// <c>two_writers_racing_on_the_same_starting_version_lose_the_second_write</c> in
    /// <see cref="FetchForWritingCompliance{TFixture,TOperations,TQuerySession}" /> — same shape,
    /// same outcome, and that is the entire point.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_cached_baseline_cannot_suppress_a_concurrency_exception()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        await using var first = OpenSession();
        await using var second = OpenSession();

        var firstStream = await EventsFor(first).FetchForWriting<CachedLedger>(streamId, 3, Cancellation);
        var secondStream = await EventsFor(second).FetchForWriting<CachedLedger>(streamId, 3, Cancellation);

        firstStream.AppendOne(new AmountPosted(1));
        await SaveChangesAsync(first);

        secondStream.AppendOne(new AmountPosted(2));
        await ShouldFailWithAsync<ConcurrencyException>(() => SaveChangesAsync(second));

        await using var reader = OpenSession();
        var ledger = await LoadDocumentAsync<CachedLedger>(reader, streamId);
        ledger.ShouldNotBeNull();
        ledger.Balance.ShouldBe(111);
    }

    [Fact]
    public async Task a_stale_expected_version_still_fails_against_a_warm_cache()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            var stream = await EventsFor(session)
                .FetchForWriting<CachedLedger>(streamId, 99, Cancellation);
            stream.AppendOne(new AmountPosted(1));
            await SaveChangesAsync(session);
        });
    }

    /// <remarks>
    /// The write-back's failure path. A commit that rolled back must not leave an entry describing
    /// state the database never took — the next fetch has to see the winner's state and not the
    /// loser's. Asserted through a fetch rather than by inspecting the cache, because a store may
    /// legitimately leave nothing behind or leave a correct entry, and both are fine.
    /// </remarks>
    [Fact]
    public async Task a_failed_commit_leaves_no_poisoned_entry_behind()
    {
        var streamId = await anOpenVaultAsync<CachedLedger>(new AmountPosted(100));
        await warmAsync<CachedLedger>(streamId, 10);

        await using (var first = OpenSession())
        await using (var second = OpenSession())
        {
            var firstStream = await EventsFor(first).FetchForWriting<CachedLedger>(streamId, 3, Cancellation);
            var secondStream = await EventsFor(second).FetchForWriting<CachedLedger>(streamId, 3, Cancellation);

            firstStream.AppendOne(new AmountPosted(1));
            await SaveChangesAsync(first);

            secondStream.AppendOne(new AmountPosted(500));
            await ShouldFailWithAsync<ConcurrencyException>(() => SaveChangesAsync(second));
        }

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedLedger>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(111);
        stream.CurrentVersion.ShouldBe(4);
    }

    #endregion

    #region The async lifecycle

    /// <remarks>
    /// The daemon never runs in this suite, so the stored snapshot for
    /// <see cref="CachedJournal" /> stays behind the stream and a fetch always has a real delta to
    /// fold. That makes these the sharper version of the facts above rather than a repetition:
    /// nothing here can pass by accident of the snapshot already being at the head.
    /// </remarks>
    [Fact]
    public async Task an_async_snapshotted_aggregate_is_cached_too()
    {
        var streamId = await anOpenVaultAsync<CachedJournal>(new AmountPosted(100));

        await warmAsync<CachedJournal>(streamId, 10);

        TheCache.WasStoredFor(typeof(CachedJournal)).ShouldBeTrue();

        var hitsBefore = TheCache.Hits;
        await warmAsync<CachedJournal>(streamId, 1);

        TheCache.Hits.ShouldBeGreaterThan(hitsBefore);
    }

    [Fact]
    public async Task an_async_cached_baseline_folds_the_delta_after_it()
    {
        var streamId = await anOpenVaultAsync<CachedJournal>(new AmountPosted(100));
        await warmAsync<CachedJournal>(streamId, 10);

        await appendFromAnotherSessionAsync<CachedJournal>(streamId, 25);
        await appendFromAnotherSessionAsync<CachedJournal>(streamId, 5);

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<CachedJournal>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(140);
        stream.CurrentVersion.ShouldBe(5);
    }

    [Fact]
    public async Task an_async_cached_baseline_cannot_suppress_a_concurrency_exception()
    {
        var streamId = await anOpenVaultAsync<CachedJournal>(new AmountPosted(100));
        await warmAsync<CachedJournal>(streamId, 10);

        await using var first = OpenSession();
        await using var second = OpenSession();

        var firstStream = await EventsFor(first).FetchForWriting<CachedJournal>(streamId, 3, Cancellation);
        var secondStream = await EventsFor(second).FetchForWriting<CachedJournal>(streamId, 3, Cancellation);

        firstStream.AppendOne(new AmountPosted(1));
        await SaveChangesAsync(first);

        secondStream.AppendOne(new AmountPosted(2));
        await ShouldFailWithAsync<ConcurrencyException>(() => SaveChangesAsync(second));
    }

    #endregion
}
