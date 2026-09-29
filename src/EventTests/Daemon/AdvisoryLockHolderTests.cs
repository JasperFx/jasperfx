using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#913 — <see cref="IAdvisoryLock"/> abstracted acquiring a distribution lock but not reading
/// who holds it, so under a store's own lock-based distribution nothing could answer "which node owns
/// this shard". <see cref="IAdvisoryLock.FindHolderAsync"/> ships as a default interface member so every
/// existing lock implementation keeps compiling; these pin what that default is allowed to do.
/// </summary>
public class AdvisoryLockHolderTests
{
    // Implements only the three acquisition members -- i.e. every IAdvisoryLock that exists today. The
    // holder read is deliberately left un-overridden: it is the default under test.
    private class AcquireOnlyLock : IAdvisoryLock
    {
        public HashSet<int> HeldIds { get; } = new();

        public bool HasLock(int lockId) => HeldIds.Contains(lockId);

        public Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
        {
            HeldIds.Add(lockId);
            return Task.FromResult(true);
        }

        public Task ReleaseLockAsync(int lockId)
        {
            HeldIds.Remove(lockId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The default is a default interface member, so it is only reachable through the interface. That is
    // exactly how a monitoring tool consumes it.
    private readonly IAdvisoryLock theLock = new AcquireOnlyLock();

    [Fact]
    public async Task the_default_refuses_rather_than_reporting_the_lock_unheld()
    {
        // The whole point. Returning null would hand a tool chasing a double-runner the single most
        // reassuring answer available -- "nothing holds this lock" -- on a store that never looked.
        await Should.ThrowAsync<NotSupportedException>(
            () => theLock.FindHolderAsync(42, CancellationToken.None));
    }

    [Fact]
    public async Task the_default_refuses_even_for_a_lock_this_node_does_hold()
    {
        // HasLock could answer this one, and the default deliberately does not fall back to it: a holder
        // read that only ever works for locks you already hold answers nothing the caller did not know.
        await theLock.TryAttainLockAsync(42, CancellationToken.None);
        theLock.HasLock(42).ShouldBeTrue();

        await Should.ThrowAsync<NotSupportedException>(
            () => theLock.FindHolderAsync(42, CancellationToken.None));
    }

    [Fact]
    public async Task an_implementing_lock_is_reached_instead_of_the_default()
    {
        var implementing = new HolderReadingLock();

        var holder = await ((IAdvisoryLock)implementing).FindHolderAsync(42, CancellationToken.None);

        holder.ShouldNotBeNull();
        holder.LockId.ShouldBe(42);
        holder.ApplicationName.ShouldBe("node-2");
        implementing.Reads.ShouldBe([42]);
    }

    [Fact]
    public async Task an_implementing_lock_reports_an_unheld_lock_as_null()
    {
        // Null is the implemented answer for "nobody holds this", which is only a usable signal because
        // the unimplemented case throws instead of sharing it.
        var implementing = new HolderReadingLock();

        var holder = await ((IAdvisoryLock)implementing).FindHolderAsync(99, CancellationToken.None);

        holder.ShouldBeNull();
        implementing.Reads.ShouldBe([99]);
    }

    [Fact]
    public void a_holder_carries_only_what_the_primitive_exposed()
    {
        // Postgres can see all of it; SQL Server's dm_tran_locks cannot. Every optional member defaults to
        // null so a store fills in its own view without having to invent the rest.
        var sparse = new AdvisoryLockHolder(42);

        sparse.LockId.ShouldBe(42);
        sparse.ApplicationName.ShouldBeNull();
        sparse.SessionId.ShouldBeNull();
        sparse.ClientAddress.ShouldBeNull();
        sparse.HeldSince.ShouldBeNull();
    }

    [Fact]
    public void is_current_node_distinguishes_not_mine_from_cannot_tell()
    {
        // Three-valued on purpose. "Another node holds it" and "I cannot tell who holds it" lead an
        // operator to opposite conclusions about whether two processes are running one shard, so a default
        // of false would be a claim the implementation never made.
        new AdvisoryLockHolder(42).IsCurrentNode.ShouldBeNull();
        new AdvisoryLockHolder(42) { IsCurrentNode = false }.IsCurrentNode.ShouldBe(false);
        new AdvisoryLockHolder(42) { IsCurrentNode = true }.IsCurrentNode.ShouldBe(true);
    }

    // What Marten and Polecat are expected to do. IAdvisoryLock is re-listed deliberately: interface
    // mapping is computed per declaring class, so a derived type that merely adds the method would still
    // bind to the default implementation.
    private sealed class HolderReadingLock : AcquireOnlyLock, IAdvisoryLock
    {
        public List<int> Reads { get; } = [];

        public Task<AdvisoryLockHolder?> FindHolderAsync(int lockId, CancellationToken token)
        {
            Reads.Add(lockId);

            // Stands in for the one row a pg_locks / sys.dm_tran_locks read either finds or does not.
            return Task.FromResult(lockId == 42
                ? new AdvisoryLockHolder(lockId)
                {
                    ApplicationName = "node-2",
                    SessionId = "8123",
                    ClientAddress = "10.0.0.7",
                    HeldSince = DateTimeOffset.UtcNow,
                    IsCurrentNode = false
                }
                : null);
        }
    }
}
