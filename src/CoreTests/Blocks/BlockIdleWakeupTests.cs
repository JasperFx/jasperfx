using System.Diagnostics;
using JasperFx.Blocks;
using Shouldly;

namespace CoreTests.Blocks;

// wolverine#4167. A Block's channel must not allow synchronous continuations: with them on, a reader
// parked in WaitToReadAsync gets resumed by TryWrite on the PUBLISHER's thread, so Post() executes the
// action inline instead of enqueuing it. These tests all failed before that flag was turned off, and
// three of the four failed on net9.0 as well -- only the bounded case was net10-specific.
public class BlockIdleWakeupTests
{
    [Fact]
    public async Task post_after_idle_does_not_process_inline_on_the_publisher()
    {
        var processed = 0;
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var block = new Block<string>(5, Block<string>.Unbounded, (item, _) =>
        {
            Interlocked.Increment(ref processed);
            if (item == "first")
            {
                firstDone.TrySetResult();
            }

            if (item == "second")
            {
                secondDone.TrySetResult();
            }

            return Task.CompletedTask;
        });

        block.Post("first");
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Let processAsync park in WaitToReadAsync. This is the idle worker.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        block.Post("second");

        Volatile.Read(ref processed).ShouldBe(1);

        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref processed).ShouldBe(2);
        block.Count.ShouldBe(0u);
    }

    [Fact]
    public async Task burst_posted_after_idle_is_fully_drained()
    {
        const int wave = 20;
        var processed = 0;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var block = new Block<int>(5, Block<int>.Unbounded, (item, _) =>
        {
            var n = Interlocked.Increment(ref processed);
            if (n == 1)
            {
                idle.TrySetResult();
            }

            if (n == 1 + wave)
            {
                done.TrySetResult();
            }

            return Task.CompletedTask;
        });

        block.Post(0);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        for (var i = 1; i <= wave; i++)
        {
            block.Post(i);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref processed).ShouldBe(1 + wave);
        block.Count.ShouldBe(0u);
    }

    /// <summary>
    /// The regression that actually bit: a whole burst executing on the publisher. Both capacities are
    /// covered because they used to differ -- an unbounded channel ran continuations inline on every
    /// runtime, a bounded one only from net10 onward.
    /// </summary>
    [Theory]
    [InlineData(Block<int>.Unbounded)]
    [InlineData(Block<int>.DefaultBoundedCapacity)]
    public async Task a_burst_posted_after_idle_never_executes_on_the_publisher(int capacity)
    {
        const int wave = 20;
        var processed = 0;
        var inline = 0;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherThread = 0;

        await using var block = new Block<int>(5, capacity, (item, _) =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref publisherThread))
            {
                Interlocked.Increment(ref inline);
            }

            var n = Interlocked.Increment(ref processed);
            if (n == 1) idle.TrySetResult();
            if (n == 1 + wave) done.TrySetResult();

            return Task.CompletedTask;
        });

        block.Post(0);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Volatile.Write(ref publisherThread, Environment.CurrentManagedThreadId);

        for (var i = 1; i <= wave; i++)
        {
            block.Post(i);
        }

        Volatile.Read(ref inline).ShouldBe(0);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref processed).ShouldBe(1 + wave);
        block.Count.ShouldBe(0u);
    }

    /// <summary>
    /// Post() enqueues; it must never absorb the action's latency. This measured 751ms for a 750ms
    /// action while continuations were synchronous.
    /// </summary>
    [Fact]
    public async Task post_does_not_absorb_the_latency_of_a_slow_action()
    {
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerThread = 0;

        await using var block = new Block<string>(5, Block<string>.Unbounded, (item, _) =>
        {
            if (item == "first")
            {
                firstDone.TrySetResult();
                return Task.CompletedTask;
            }

            Volatile.Write(ref handlerThread, Environment.CurrentManagedThreadId);
            Thread.Sleep(750);
            slowDone.TrySetResult();
            return Task.CompletedTask;
        });

        block.Post("first");
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var publisher = Environment.CurrentManagedThreadId;
        var stopwatch = Stopwatch.StartNew();
        block.Post("slow");
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(250);

        await slowDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Volatile.Read(ref handlerThread).ShouldNotBe(publisher);
    }
}
