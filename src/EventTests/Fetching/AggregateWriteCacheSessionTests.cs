using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Fetching;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Fetching;

public class Tab
{
    public decimal Balance { get; set; }

    public static Tab FoldFrom(Tab? baseline, IEnumerable<decimal> amounts)
    {
        var tab = baseline ?? new Tab();
        foreach (var amount in amounts)
        {
            tab.Balance += amount;
        }

        return tab;
    }
}

/// <summary>
/// The shape every store's write path has, over a list of amounts: read the version, take a baseline,
/// fold the delta, register, then check the version, write, and flush or discard. Enough of a store to
/// assert on the thing that matters, which is whether the next fetch folds to the right answer.
/// </summary>
public class FakeWriteStore(IAggregateWriteCache cache)
{
    private readonly Dictionary<(string Tenant, Guid Id), List<decimal>> _streams = new();

    public IAggregateWriteCache Cache { get; } = cache;

    public AggregateCacheKey KeyFor(Guid id, string tenant = "acme") => new(typeof(Tab), "db-1", tenant, id);

    public List<decimal> Stream(Guid id, string tenant = "acme")
    {
        if (!_streams.TryGetValue((tenant, id), out var stream))
        {
            stream = [];
            _streams[(tenant, id)] = stream;
        }

        return stream;
    }

    public decimal ColdBalance(Guid id, string tenant = "acme") => Stream(id, tenant).Sum();

    public FakeUnitOfWork Open(string tenant = "acme") => new(this, tenant);

    /// <summary>One fetch, append and commit, which is what leaves an entry behind.</summary>
    public void Write(Guid id, decimal amount, string tenant = "acme")
    {
        var work = Open(tenant);
        work.Fetch(id);
        work.Append(amount);
        work.Commit();
    }
}

public class FakeUnitOfWork(FakeWriteStore store, string tenant)
{
    private readonly AggregateWriteCacheSession _writes = new();
    private readonly List<decimal> _appended = [];
    private Guid _id;
    private long _fetchedVersion;

    public Tab? Aggregate { get; private set; }

    public long ReadFrom { get; private set; }

    public void Fetch(Guid id)
    {
        _id = id;

        var stream = store.Stream(id, tenant);
        _fetchedVersion = stream.Count;

        var baseline = _writes.TakeBaseline<Tab>(store.Cache, store.KeyFor(id, tenant), _fetchedVersion);
        ReadFrom = baseline.FirstVersionToRead;

        Aggregate = Tab.FoldFrom(baseline.Aggregate, stream.Skip((int)baseline.Version));
        _writes.Register(baseline, Aggregate, _fetchedVersion);
    }

    public void Append(decimal amount) => _appended.Add(amount);

    public void Commit()
    {
        // Only a stream being appended to is guarded, so a session that fetched this one and committed
        // nothing to it can finish after any number of newer writers.
        var stream = store.Stream(_id, tenant);
        if (_appended.Count > 0 && stream.Count != _fetchedVersion)
        {
            _writes.Discard();
            throw new InvalidOperationException("Concurrency failure");
        }

        stream.AddRange(_appended);
        _appended.Clear();
        _writes.Flush();
    }
}

public class AggregateWriteBaselineTests
{
    private readonly AggregateWriteCacheSession theSession = new();
    private readonly RecentlyUsedAggregateWriteCache theCache = new();
    private readonly AggregateCacheKey theKey = new(typeof(Tab), "db-1", "acme", Guid.NewGuid());

    [Fact]
    public void a_miss_reads_the_whole_stream()
    {
        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 5);

        baseline.HasAggregate.ShouldBeFalse();
        baseline.Version.ShouldBe(0);
        baseline.FirstVersionToRead.ShouldBe(1);
    }

    [Fact]
    public void a_hit_reads_from_after_the_baseline_and_owns_the_instance()
    {
        var tab = new Tab();
        theCache.Store(theKey, tab, 3);

        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 5);

        baseline.Aggregate.ShouldBeSameAs(tab);
        baseline.Version.ShouldBe(3);
        baseline.FirstVersionToRead.ShouldBe(4);

        theCache.TryTake(theKey, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void a_baseline_exactly_at_the_stream_head_is_usable()
    {
        theCache.Store(theKey, new Tab(), 5);

        theSession.TakeBaseline<Tab>(theCache, theKey, 5).HasAggregate.ShouldBeTrue();
    }

    /// <remarks>
    /// What a restore or a rolled-back transaction leaves behind. Folding from version 501 would read
    /// nothing and hand back state the database does not have. The entry must not survive either, or
    /// the same refusal happens on every fetch.
    /// </remarks>
    [Fact]
    public void a_baseline_ahead_of_the_stream_is_refused_and_gone()
    {
        theCache.Store(theKey, new Tab { Balance = 9999 }, 500);

        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 3);

        baseline.HasAggregate.ShouldBeFalse();
        baseline.FirstVersionToRead.ShouldBe(1);
        theCache.TryTake(theKey, out _, out _).ShouldBeFalse();
    }

    /// <remarks>
    /// Polecat and Fisher take only when the version is positive, so a tombstoned stream's entry stays
    /// put and is accepted as a baseline by a new stream with the same id once it gets past that version.
    /// </remarks>
    [Fact]
    public void a_fetch_of_a_stream_that_no_longer_exists_removes_its_entry()
    {
        theCache.Store(theKey, new Tab { Balance = 9999 }, 10);

        theSession.TakeBaseline<Tab>(theCache, theKey, 0).HasAggregate.ShouldBeFalse();

        theCache.TryTake(theKey, out _, out _).ShouldBeFalse();
    }

    /// <remarks>
    /// Marten's batched read has to take before it knows the stream version, so that overload hands the
    /// snapshot over and the store compares. The refusal above must not be baked into it.
    /// </remarks>
    [Fact]
    public void a_take_with_no_stream_version_leaves_the_comparison_to_the_store()
    {
        theCache.Store(theKey, new Tab(), 500);

        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey);

        baseline.HasAggregate.ShouldBeTrue();
        baseline.Version.ShouldBe(500);
    }

    /// <remarks>
    /// Polecat and Marten's Async plan read <c>version + 1</c> from it, which is the whole stream, and
    /// fold that onto a non-null baseline: every event applied twice.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void a_baseline_claiming_no_version_is_refused(long version)
    {
        theCache.Store(theKey, new Tab { Balance = 100 }, version);

        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 5);

        baseline.HasAggregate.ShouldBeFalse();
        baseline.FirstVersionToRead.ShouldBe(1);
    }

    /// <remarks>Marten's hard cast raised this from inside FetchForWriting.</remarks>
    [Fact]
    public void an_entry_of_another_type_is_a_miss_rather_than_a_cast_failure()
    {
        theCache.Store(theKey, new Basket(), 3);

        theSession.TakeBaseline<Tab>(theCache, theKey, 5).HasAggregate.ShouldBeFalse();
    }

    [Fact]
    public void a_default_baseline_is_inert()
    {
        // What a FetchManyForWriting plan array holds for a stream that does not exist.
        AggregateWriteBaseline<Tab> baseline = default;

        baseline.HasAggregate.ShouldBeFalse();
        baseline.FirstVersionToRead.ShouldBe(1);
        baseline.WithoutAggregate().HasAggregate.ShouldBeFalse();

        theSession.Register(baseline, new Tab(), 5);

        theSession.HasPendingWrites.ShouldBeFalse();
    }

    [Fact]
    public void events_past_the_baseline_that_the_read_did_not_return_were_archived()
    {
        theCache.Store(theKey, new Tab(), 3);
        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 5);

        baseline.NeedsArchivalRefetch([], 5).ShouldBeTrue();
    }

    [Fact]
    public void a_read_that_returned_something_or_had_nothing_to_return_is_not_archival()
    {
        theCache.Store(theKey, new Tab(), 5);
        var atHead = theSession.TakeBaseline<Tab>(theCache, theKey, 5);

        atHead.NeedsArchivalRefetch([], 5).ShouldBeFalse();
        atHead.NeedsArchivalRefetch([Substitute.For<IEvent>()], 7).ShouldBeFalse();

        theSession.TakeBaseline<Tab>(theCache, theKey, 5).NeedsArchivalRefetch([], 5).ShouldBeFalse();
    }

    /// <remarks>
    /// A fetch redone from nothing is the moment the cache can be repaired, so giving up the snapshot
    /// must not give up the write-back.
    /// </remarks>
    [Fact]
    public void a_baseline_given_up_still_writes_back()
    {
        theCache.Store(theKey, new Tab(), 3);
        var baseline = theSession.TakeBaseline<Tab>(theCache, theKey, 5).WithoutAggregate();

        baseline.HasAggregate.ShouldBeFalse();
        baseline.FirstVersionToRead.ShouldBe(1);

        var rebuilt = new Tab();
        theSession.Register(baseline, rebuilt, 5);
        theSession.Flush();

        theCache.TryTake(theKey, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(rebuilt);
        version.ShouldBe(5);
    }
}

public class AggregateWriteCacheSessionTests
{
    private readonly RecentlyUsedAggregateWriteCache theCache = new();
    private readonly FakeWriteStore theStore;
    private readonly Guid theId = Guid.NewGuid();

    public AggregateWriteCacheSessionTests()
    {
        theStore = new FakeWriteStore(theCache);
        theStore.Stream(theId).Add(100);
    }

    private static AggregateWriteBaseline<Tab> missFor(AggregateWriteCacheSession session, IAggregateWriteCache cache,
        AggregateCacheKey key) => session.TakeBaseline<Tab>(cache, key, 1);

    #region Nothing reaches the cache before the flush

    /// <remarks>
    /// Marten's Async plan stores the instance it is about to return. A second session then claims that
    /// instance and folds onto it while the first is still using it to decide what to write.
    /// </remarks>
    [Fact]
    public void an_aggregate_is_not_reachable_while_its_caller_still_holds_it()
    {
        var holder = theStore.Open();
        holder.Fetch(theId);

        var other = theStore.Open();
        theStore.Stream(theId).Add(5);
        other.Fetch(theId);

        other.Aggregate.ShouldNotBeSameAs(holder.Aggregate);
        holder.Aggregate!.Balance.ShouldBe(100);
    }

    [Fact]
    public void a_fetch_that_never_commits_leaves_nothing_behind()
    {
        theStore.Open().Fetch(theId);

        theCache.TryTake(theStore.KeyFor(theId), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void a_new_stream_stores_nothing()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(Guid.NewGuid());

        session.Register(session.TakeBaseline<Tab>(theCache, key, 0), new Tab(), 0);

        session.HasPendingWrites.ShouldBeFalse();
    }

    [Fact]
    public void a_null_aggregate_stores_nothing()
    {
        var session = new AggregateWriteCacheSession();

        session.Register(missFor(session, theCache, theStore.KeyFor(theId)), null, 1);

        session.HasPendingWrites.ShouldBeFalse();
    }

    /// <remarks>
    /// Marten, Polecat and Fisher each keep this path allocation free in their own way. The session has
    /// to hold no list and retain no instance for a type nobody enrolled.
    /// </remarks>
    [Fact]
    public void an_aggregate_type_nobody_enrolled_is_not_retained()
    {
        var session = new AggregateWriteCacheSession();
        var cache = NulloAggregateWriteCache.Instance;

        session.Register(missFor(session, cache, theStore.KeyFor(theId)), new Tab(), 1);

        session.HasPendingWrites.ShouldBeFalse();
    }

    #endregion

    #region The entry says what the instance contains

    /// <remarks>
    /// A baseline read before the append has to stay labelled as such. Labelling it with the committed
    /// version, as the Marten listener does, makes the next fetch skip the session's own events: 10
    /// would vanish from the answer here.
    /// </remarks>
    [Fact]
    public void the_events_this_session_appended_are_read_on_the_next_fetch()
    {
        theStore.Write(theId, 10);

        theCache.TryTake(theStore.KeyFor(theId), out var entry, out var version).ShouldBeTrue();
        version.ShouldBe(1);
        ((Tab)entry).Balance.ShouldBe(100);
        theCache.Store(theStore.KeyFor(theId), entry, version);

        var next = theStore.Open();
        next.Fetch(theId);

        next.ReadFrom.ShouldBe(2);
        next.Aggregate!.Balance.ShouldBe(theStore.ColdBalance(theId));
    }

    [Fact]
    public void many_rounds_through_the_cache_stay_equal_to_a_cold_fold()
    {
        for (var round = 1; round <= 20; round++)
        {
            theStore.Write(theId, round);

            var check = theStore.Open();
            check.Fetch(theId);
            check.Aggregate!.Balance.ShouldBe(theStore.ColdBalance(theId));
            check.Commit();
        }
    }

    [Fact]
    public void events_another_writer_appended_after_the_baseline_are_folded_on()
    {
        theStore.Write(theId, 10);
        theStore.Stream(theId).Add(25);

        var next = theStore.Open();
        next.Fetch(theId);

        next.Aggregate!.Balance.ShouldBe(135);
    }

    /// <remarks>
    /// Marten's listener keeps the instance FetchForWriting returned and relabels it, so an aggregate
    /// whose Apply returns a new instance, and any aggregate with UseIdentityMapForAggregates off, is
    /// cached at the committed version without the committed events. The exact version match then
    /// serves it. The instance the projection produced is the only one that can claim that version.
    /// </remarks>
    [Fact]
    public void advancing_writes_back_the_instance_the_projection_produced()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);
        var fetched = new Tab { Balance = 100 };
        var projected = new Tab { Balance = 110 };

        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), fetched, 1);
        session.Advance(key, projected, 2).ShouldBeTrue();
        session.Flush();

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(projected);
        version.ShouldBe(2);
    }

    [Fact]
    public void only_advancing_moves_the_label()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(theCache, key, 4), new Tab(), 4);
        session.Flush();

        theCache.TryTake(key, out _, out var version).ShouldBeTrue();
        version.ShouldBe(4);
    }

    [Fact]
    public void advancing_a_stream_nothing_registered_is_a_no_op()
    {
        var session = new AggregateWriteCacheSession();

        // Every inline projection in a store goes past this, for every aggregate, enrolled or not.
        session.Advance(theStore.KeyFor(theId), new Tab(), 2).ShouldBeFalse();
        session.Flush();

        theCache.TryTake(theStore.KeyFor(theId), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void advancing_never_moves_a_write_backwards()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);
        var current = new Tab();

        session.Register(session.TakeBaseline<Tab>(theCache, key, 5), current, 5);
        session.Advance(key, new Tab(), 3).ShouldBeFalse();
        session.Flush();

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(current);
        version.ShouldBe(5);
    }

    /// <remarks>
    /// With an aggregate identity map on, Polecat's ProjectLatest folds pending events onto the
    /// registered instance before the commit, so its label is already a lie and the next fetch folds
    /// those events a second time.
    /// </remarks>
    [Fact]
    public void an_instance_that_stopped_matching_its_version_can_be_dropped_on_its_own()
    {
        var session = new AggregateWriteCacheSession();
        var dropped = theStore.KeyFor(theId);
        var kept = theStore.KeyFor(Guid.NewGuid());

        session.Register(session.TakeBaseline<Tab>(theCache, dropped, 1), new Tab(), 1);
        session.Register(session.TakeBaseline<Tab>(theCache, kept, 1), new Tab(), 1);

        session.Discard(dropped);
        session.Flush();

        theCache.TryTake(dropped, out _, out _).ShouldBeFalse();
        theCache.TryTake(kept, out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void a_second_fetch_of_a_stream_supersedes_the_first()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);
        var second = new Tab();

        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), new Tab(), 1);
        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), second, 1);
        session.Flush();

        theCache.TryTake(key, out var aggregate, out _).ShouldBeTrue();
        aggregate.ShouldBeSameAs(second);
    }

    #endregion

    #region A failed or rolled back commit

    [Fact]
    public void a_failed_commit_writes_nothing_back()
    {
        var cache = Substitute.For<IAggregateWriteCache>();
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(cache, key, 1), new Tab(), 1);
        session.Discard();
        session.Flush();

        cache.ReceivedCalls().Count(x => x.GetMethodInfo().Name != nameof(IAggregateWriteCache.TryTake))
            .ShouldBe(0);
    }

    [Fact]
    public void the_next_fetch_after_a_lost_race_sees_the_winner_and_not_the_loser()
    {
        theStore.Write(theId, 10);

        var loser = theStore.Open();
        loser.Fetch(theId);
        loser.Append(500);

        theStore.Stream(theId).Add(1);

        Should.Throw<InvalidOperationException>(() => loser.Commit());

        var next = theStore.Open();
        next.Fetch(theId);

        next.Aggregate!.Balance.ShouldBe(111);
    }

    /// <remarks>
    /// Marten and Fisher leave a failed commit's entries in place on the assumption that the session is
    /// about to die. A session that carries on, as EjectAllPendingChanges invites, published them on its
    /// next success. For Marten that instance has the rolled-back events applied and a label matching the
    /// stream head, so it is served as current.
    /// </remarks>
    [Fact]
    public void a_commit_that_succeeds_after_a_failed_one_does_not_publish_the_failed_ones_entries()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), new Tab { Balance = 600 }, 1);
        session.Discard();

        session.Flush();

        theCache.TryTake(key, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void a_failed_commit_drops_an_advance_that_never_became_true()
    {
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), new Tab(), 1);
        session.Advance(key, new Tab { Balance = 600 }, 2);
        session.Discard();
        session.Flush();

        theCache.TryTake(key, out _, out _).ShouldBeFalse();
    }

    /// <remarks>Take-on-read means the caller's claim costs the entry. Nothing brings it back.</remarks>
    [Fact]
    public void a_discarded_baseline_is_not_put_back()
    {
        theStore.Write(theId, 10);

        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);
        var baseline = session.TakeBaseline<Tab>(theCache, key, 2);
        baseline.Aggregate!.Balance = 999;

        session.Register(baseline, baseline.Aggregate, 2);
        session.Discard();

        theCache.TryTake(key, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void an_unrelated_entry_is_untouched_by_a_failed_commit()
    {
        var unrelated = theStore.KeyFor(Guid.NewGuid());
        var tab = new Tab();
        theCache.Store(unrelated, tab, 9);

        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);
        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), new Tab(), 1);
        session.Discard();

        theCache.TryTake(unrelated, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(tab);
        version.ShouldBe(9);
    }

    #endregion

    #region The flush

    [Fact]
    public void a_flush_writes_once()
    {
        var cache = Substitute.For<IAggregateWriteCache>();
        var session = new AggregateWriteCacheSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(cache, key, 1), new Tab(), 1);
        session.Flush();
        session.Flush();

        cache.Received(1).StoreIfNewer(key, Arg.Any<object>(), 1);
        session.HasPendingWrites.ShouldBeFalse();
    }

    /// <remarks>
    /// The commit has happened. Letting this out would have SaveChangesAsync report a committed unit of
    /// work as failed and invite a retry, for the sake of an entry that was only ever an optimization.
    /// </remarks>
    [Fact]
    public void a_cache_that_throws_cannot_fail_a_commit_that_already_happened()
    {
        var failing = Substitute.For<IAggregateWriteCache>();
        failing.StoreIfNewer(Arg.Any<AggregateCacheKey>(), Arg.Any<object>(), Arg.Any<long>())
            .Throws(new InvalidOperationException("the cache is broken"));

        var session = new AggregateWriteCacheSession();
        var other = theStore.KeyFor(Guid.NewGuid());

        session.Register(session.TakeBaseline<Tab>(failing, theStore.KeyFor(theId), 1), new Tab(), 1);
        session.Register(session.TakeBaseline<Tab>(theCache, other, 1), new Tab(), 1);

        Should.NotThrow(() => session.Flush());

        theCache.TryTake(other, out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void a_session_hears_about_every_aggregate_it_has_finished_with()
    {
        var session = new HandingOffSession();
        var first = theStore.KeyFor(theId);
        var second = theStore.KeyFor(Guid.NewGuid());

        session.Register(session.TakeBaseline<Tab>(theCache, first, 1), new Tab(), 1);
        session.Register(session.TakeBaseline<Tab>(theCache, second, 1), new Tab(), 1);
        session.Flush();

        session.Published.ShouldBe([first, second], ignoreOrder: true);
    }

    /// <remarks>
    /// Being refused does not change that the caller is done with it, and holding on to the instance is
    /// the thing the hook exists to stop.
    /// </remarks>
    [Fact]
    public void a_session_hears_about_an_aggregate_the_cache_refused()
    {
        var session = new HandingOffSession();
        var key = theStore.KeyFor(theId);

        session.Register(session.TakeBaseline<Tab>(theCache, key, 1), new Tab(), 1);
        theCache.Store(key, new Tab(), 7);
        session.Flush();

        session.Published.ShouldBe([key]);
    }

    [Fact]
    public void a_discarded_aggregate_was_never_published()
    {
        var session = new HandingOffSession();

        session.Register(session.TakeBaseline<Tab>(theCache, theStore.KeyFor(theId), 1), new Tab(), 1);
        session.Discard();
        session.Flush();

        session.Published.ShouldBeEmpty();
    }

    private sealed class HandingOffSession: AggregateWriteCacheSession
    {
        public List<AggregateCacheKey> Published { get; } = [];

        protected override void OnPublished(AggregateCacheKey key) => Published.Add(key);
    }

    #endregion

    #region Concurrent writers

    /// <remarks>
    /// The slow session read version 1. Its entry is true of version 1, and replacing the newer writer's
    /// entry with it would throw away the work the cache exists to save, and for Marten's exact version
    /// match would turn every later fetch into a miss.
    /// </remarks>
    [Fact]
    public void a_slow_session_finishing_late_does_not_replace_a_newer_writers_entry()
    {
        var slow = theStore.Open();
        slow.Fetch(theId);

        theStore.Write(theId, 10);
        theStore.Write(theId, 20);

        var key = theStore.KeyFor(theId);
        theCache.TryTake(key, out var newer, out var newerVersion).ShouldBeTrue();
        newerVersion.ShouldBe(2);
        theCache.Store(key, newer, newerVersion);

        slow.Commit();

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(newer);
        version.ShouldBe(2);
    }

    [Fact]
    public void the_newer_of_two_flushes_wins_in_either_order()
    {
        var key = theStore.KeyFor(theId);
        var older = new AggregateWriteCacheSession();
        var newer = new AggregateWriteCacheSession();
        var newest = new Tab();

        older.Register(older.TakeBaseline<Tab>(theCache, key, 5), new Tab(), 5);
        newer.Register(newer.TakeBaseline<Tab>(theCache, key, 8), newest, 8);

        older.Flush();
        newer.Flush();

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(newest);
        version.ShouldBe(8);
    }

    [Fact]
    public void a_concurrent_take_leaves_a_later_flush_free_to_store()
    {
        var key = theStore.KeyFor(theId);
        var session = new AggregateWriteCacheSession();
        var refreshed = new Tab();

        session.Register(session.TakeBaseline<Tab>(theCache, key, 3), refreshed, 3);

        // Somebody newer wrote, and somebody else claimed it. The cache holds nothing to protect.
        theCache.Store(key, new Tab(), 9);
        theCache.TryTake(key, out _, out _).ShouldBeTrue();

        session.Flush();

        theCache.TryTake(key, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(refreshed);
        version.ShouldBe(3);
    }

    #endregion

    #region Tenants

    [Fact]
    public void two_tenants_with_the_same_stream_id_never_share_an_entry()
    {
        theStore.Stream(theId, "globex").Add(7);

        theStore.Write(theId, 10);
        theStore.Write(theId, 20, "globex");

        var acme = theStore.Open();
        acme.Fetch(theId);

        var globex = theStore.Open("globex");
        globex.Fetch(theId);

        acme.Aggregate!.Balance.ShouldBe(110);
        globex.Aggregate!.Balance.ShouldBe(27);
    }

    [Fact]
    public void one_tenant_writing_back_is_not_a_hit_for_another()
    {
        theStore.Write(theId, 10);

        theStore.Stream(theId, "globex").Add(7);
        var globex = theStore.Open("globex");
        globex.Fetch(theId);

        globex.ReadFrom.ShouldBe(1);
    }

    /// <remarks>
    /// Marten's listener finds the committed stream by id alone. One session appending to the same
    /// stream id under two tenants (ForTenant, conjoined) can stamp one tenant's aggregate with the
    /// other's version. Advancing by key cannot reach across.
    /// </remarks>
    [Fact]
    public void advancing_one_tenants_stream_leaves_anothers_with_the_same_id_alone()
    {
        var session = new AggregateWriteCacheSession();
        var acme = theStore.KeyFor(theId);
        var globex = theStore.KeyFor(theId, "globex");
        var globexTab = new Tab();

        session.Register(session.TakeBaseline<Tab>(theCache, acme, 2), new Tab(), 2);
        session.Register(session.TakeBaseline<Tab>(theCache, globex, 5), globexTab, 5);

        session.Advance(acme, new Tab(), 3);
        session.Flush();

        theCache.TryTake(acme, out _, out var acmeVersion).ShouldBeTrue();
        acmeVersion.ShouldBe(3);

        theCache.TryTake(globex, out var aggregate, out var globexVersion).ShouldBeTrue();
        aggregate.ShouldBeSameAs(globexTab);
        globexVersion.ShouldBe(5);
    }

    [Fact]
    public void one_database_writing_back_is_not_a_hit_for_another()
    {
        var session = new AggregateWriteCacheSession();
        var first = new AggregateCacheKey(typeof(Tab), "db-1", "acme", theId);
        var second = new AggregateCacheKey(typeof(Tab), "db-2", "acme", theId);

        session.Register(session.TakeBaseline<Tab>(theCache, first, 1), new Tab(), 1);
        session.Flush();

        session.TakeBaseline<Tab>(theCache, second, 1).HasAggregate.ShouldBeFalse();
    }

    #endregion
}

public class StoreIfNewerTests
{
    private readonly RecentlyUsedAggregateWriteCache theCache = new();
    private readonly AggregateCacheKey theKey = new(typeof(Tab), "db-1", "acme", Guid.NewGuid());

    [Fact]
    public void an_empty_key_takes_the_write()
    {
        theCache.StoreIfNewer(theKey, new Tab(), 3).ShouldBeTrue();

        theCache.TryTake(theKey, out _, out var version).ShouldBeTrue();
        version.ShouldBe(3);
    }

    [Fact]
    public void a_later_version_replaces_an_earlier_one()
    {
        var later = new Tab();
        theCache.Store(theKey, new Tab(), 3);

        theCache.StoreIfNewer(theKey, later, 4).ShouldBeTrue();

        theCache.TryTake(theKey, out var aggregate, out _).ShouldBeTrue();
        aggregate.ShouldBeSameAs(later);
    }

    [Fact]
    public void an_earlier_version_is_refused()
    {
        var existing = new Tab();
        theCache.Store(theKey, existing, 4);

        theCache.StoreIfNewer(theKey, new Tab(), 3).ShouldBeFalse();

        theCache.TryTake(theKey, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(existing);
        version.ShouldBe(4);
    }

    [Fact]
    public void the_same_version_takes_the_new_instance()
    {
        var replacement = new Tab();
        theCache.Store(theKey, new Tab(), 4);

        theCache.StoreIfNewer(theKey, replacement, 4).ShouldBeTrue();

        theCache.TryTake(theKey, out var aggregate, out _).ShouldBeTrue();
        aggregate.ShouldBeSameAs(replacement);
    }

    [Fact]
    public void a_null_aggregate_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => theCache.StoreIfNewer(theKey, null!, 1));
    }

    [Fact]
    public void it_stays_bounded()
    {
        var cache = new RecentlyUsedAggregateWriteCache(5);
        var keys = Enumerable.Range(0, 50)
            .Select(_ => new AggregateCacheKey(typeof(Tab), "db-1", "acme", Guid.NewGuid())).ToArray();

        foreach (var key in keys)
        {
            cache.StoreIfNewer(key, new Tab(), 1);
        }

        keys.Count(key => cache.TryTake(key, out _, out _)).ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public void a_refused_write_does_not_read_as_a_take()
    {
        theCache.Store(theKey, new Tab(), 4);

        theCache.StoreIfNewer(theKey, new Tab(), 3);

        theCache.Hits.ShouldBe(0);
        theCache.Misses.ShouldBe(0);
    }

    /// <remarks>
    /// Without the write lock the check and the store interleave, and the last thread through wins
    /// whatever its version was.
    /// </remarks>
    [Fact]
    public void the_highest_version_survives_writers_racing_in_any_order()
    {
        for (var round = 0; round < 200; round++)
        {
            var key = new AggregateCacheKey(typeof(Tab), "db-1", "acme", Guid.NewGuid());
            var cache = new RecentlyUsedAggregateWriteCache();

            Parallel.For(1, 33, new ParallelOptions { MaxDegreeOfParallelism = 16 },
                version => cache.StoreIfNewer(key, new Tab(), version));

            cache.TryTake(key, out _, out var survivor).ShouldBeTrue();
            survivor.ShouldBe(32);
        }
    }

    [Fact]
    public void the_nullo_cache_stores_nothing()
    {
        var cache = NulloAggregateWriteCache.Instance;

        cache.StoreIfNewer(theKey, new Tab(), 5).ShouldBeFalse();
        cache.TryTake(theKey, out _, out _).ShouldBeFalse();
    }

    /// <remarks>
    /// Everything implementing the three-member interface today, including the fakes in the store test
    /// suites and anyone's IMemoryCache adapter, lands on the default and has to get the guard too.
    /// </remarks>
    [Fact]
    public void an_implementation_that_never_heard_of_it_gets_the_guard_from_the_default()
    {
        IAggregateWriteCache cache = new ThreeMemberCache();
        var existing = new Tab();
        cache.Store(theKey, existing, 4);

        cache.StoreIfNewer(theKey, new Tab(), 3).ShouldBeFalse();

        cache.TryTake(theKey, out var aggregate, out var version).ShouldBeTrue();
        aggregate.ShouldBeSameAs(existing);
        version.ShouldBe(4);
    }

    [Fact]
    public void the_default_takes_a_newer_write()
    {
        IAggregateWriteCache cache = new ThreeMemberCache();
        var newer = new Tab();
        cache.Store(theKey, new Tab(), 4);

        cache.StoreIfNewer(theKey, newer, 5).ShouldBeTrue();

        cache.TryTake(theKey, out var aggregate, out _).ShouldBeTrue();
        aggregate.ShouldBeSameAs(newer);
    }

    [Fact]
    public void the_default_takes_a_write_to_an_empty_key()
    {
        IAggregateWriteCache cache = new ThreeMemberCache();

        cache.StoreIfNewer(theKey, new Tab(), 1).ShouldBeTrue();

        cache.TryTake(theKey, out _, out var version).ShouldBeTrue();
        version.ShouldBe(1);
    }

    /// <remarks>
    /// Hits is what the compliance suite uses to tell a store that consulted the cache from one that
    /// ignored the opt-in. Left on the default, a write-back would be a take, and a store that never read
    /// the cache would look like one that did.
    /// </remarks>
    [Fact]
    public void the_compliance_recording_cache_does_not_count_a_write_back_as_a_take()
    {
        var cache = new RecordingAggregateWriteCache();
        var session = new AggregateWriteCacheSession();

        cache.Seed(theKey, new Tab(), 4);

        session.Register(session.TakeBaseline<Tab>(cache, theKey, 4), new Tab(), 4);
        var hitsAfterTheFetch = cache.Hits;

        cache.Seed(theKey, new Tab(), 9);
        session.Flush();

        cache.Hits.ShouldBe(hitsAfterTheFetch);
        cache.TakenKeys.Count.ShouldBe(1);
    }

    [Fact]
    public void the_compliance_recording_cache_records_only_what_it_stored()
    {
        var recording = new RecordingAggregateWriteCache();
        IAggregateWriteCache cache = recording;

        cache.StoreIfNewer(theKey, new Tab(), 4).ShouldBeTrue();
        cache.StoreIfNewer(theKey, new Tab(), 3).ShouldBeFalse();

        recording.StoredKeys.Count.ShouldBe(1);
    }

    private sealed class ThreeMemberCache: IAggregateWriteCache
    {
        private readonly Dictionary<AggregateCacheKey, (object Aggregate, long Version)> _entries = new();

        public bool TryTake(AggregateCacheKey key, out object? aggregate, out long version)
        {
            if (_entries.Remove(key, out var entry))
            {
                aggregate = entry.Aggregate;
                version = entry.Version;
                return true;
            }

            aggregate = default;
            version = 0;
            return false;
        }

        public void Store(AggregateCacheKey key, object aggregate, long version)
            => _entries[key] = (aggregate, version);

        public void Evict(AggregateCacheKey key) => _entries.Remove(key);
    }
}
