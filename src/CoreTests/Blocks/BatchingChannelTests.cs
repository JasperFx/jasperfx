using JasperFx.Blocks;
using JasperFx.Core;
using Shouldly;
using Xunit;

namespace CoreTests.Blocks;

// wolverine#3490: the flush timer was reset on every Post, making the timeout a quiet-period
// debounce. A steady trickle arriving faster than the timeout postponed the flush indefinitely
// until batchSize accumulated — measured as multi-second p50 delivery latency at 8 msg/s with
// Wolverine's default (100, 250ms) sender batching. The timeout is now the maximum age of a
// batch, armed by the batch's first item and untouched by later ones.
public class BatchingChannelTests
{
    private static (BatchingChannel<int>, List<int[]>, TaskCompletionSource) channelWithCapture(
        TimeSpan timeout, int batchSize)
    {
        var batches = new List<int[]>();
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downstream = new Block<int[]>(batch =>
        {
            lock (batches)
            {
                batches.Add(batch);
            }

            firstBatch.TrySetResult();
        });

        return (new BatchingChannel<int>(timeout, downstream, batchSize), batches, firstBatch);
    }

    [Fact]
    public async Task steady_trickle_faster_than_the_timeout_still_flushes_within_the_max_age()
    {
        var (channel, batches, firstBatch) = channelWithCapture(250.Milliseconds(), 100);

        // 20 items at 25ms intervals: every gap is well under the 250ms timeout, and 20 < 100
        // batch size. Under the old debounce semantics nothing would flush until the trickle
        // stopped; under max-age semantics the first batch must land at ~250ms.
        var posting = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                channel.Post(i);
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));
        flushed.ShouldBe(firstBatch.Task,
            "the first batch should flush within the max age even though items keep trickling in");

        await posting;
        channel.Complete();
        await channel.WaitForCompletionAsync();

        lock (batches)
        {
            batches.SelectMany(x => x).OrderBy(x => x).ShouldBe(Enumerable.Range(0, 20));
        }
    }

    [Fact]
    public async Task the_trailing_batch_is_delivered_exactly_once_when_completion_races_the_timer()
    {
        // WaitForCompletionAsync used to read and post the trailing partial batch WITHOUT taking
        // _syncLock and without clearing it, while the flush timer armed by that batch's first item was
        // still live. When the timer fired at the same moment, TriggerBatch posted and cleared the same
        // items the completion drain was also posting, so the batch shipped TWICE. It showed up as an
        // intermittent CI failure of the trickle test above, on net9.0 only, with items 10-19 duplicated.
        //
        // The race is timing-dependent, so this drives it repeatedly with Complete() landing right on
        // top of the timer's deadline. Any duplicate at all is a failure — this asserts exactly-once,
        // not merely "everything arrived".
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var (channel, batches, _) = channelWithCapture(20.Milliseconds(), 100);

            for (var i = 0; i < 5; i++)
            {
                channel.Post(i);
            }

            // Land the completion drain in the timer's window rather than safely before or after it.
            await Task.Delay(20, TestContext.Current.CancellationToken);

            channel.Complete();
            await channel.WaitForCompletionAsync();

            lock (batches)
            {
                batches.SelectMany(x => x).OrderBy(x => x)
                    .ShouldBe(Enumerable.Range(0, 5), $"attempt {attempt} delivered a duplicated batch");
            }
        }
    }

    [Fact]
    public async Task lone_item_flushes_after_the_timeout()
    {
        var (channel, batches, firstBatch) = channelWithCapture(100.Milliseconds(), 100);

        channel.Post(42);

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));
        flushed.ShouldBe(firstBatch.Task);

        channel.Complete();
        await channel.WaitForCompletionAsync();

        lock (batches)
        {
            batches.SelectMany(x => x).ShouldBe([42]);
        }
    }

    [Fact]
    public async Task reaching_the_batch_size_flushes_immediately()
    {
        var (channel, batches, firstBatch) = channelWithCapture(10.Minutes(), 10);

        for (var i = 0; i < 10; i++)
        {
            channel.Post(i);
        }

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));
        flushed.ShouldBe(firstBatch.Task, "a full batch should flush without waiting on the timer");

        channel.Complete();
        await channel.WaitForCompletionAsync();

        lock (batches)
        {
            batches.SelectMany(x => x).OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));
        }
    }

    // jasperfx#1095: Post hands the item to the inner block, which adds it on its own consumer, so a TriggerBatch
    // straight after a Post found an empty batch and did nothing. The item then armed the timer and waited out the
    // whole timeout. Asserting on the elapsed time is the point: the item arrives either way.
    [Fact]
    public async Task trigger_batch_right_after_post_flushes_without_waiting_for_the_timeout()
    {
        for (var i = 0; i < 10; i++)
        {
            var (channel, batches, firstBatch) = channelWithCapture(30.Seconds(), 100);

            channel.Post(i);
            channel.TriggerBatch();

            var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));
            flushed.ShouldBe(firstBatch.Task, "TriggerBatch must cover the item posted before it");

            lock (batches)
            {
                batches.ShouldHaveSingleItem().ShouldBe([i]);
            }

            channel.Complete();
        }
    }

    [Fact]
    public async Task trigger_batch_right_after_post_async_flushes_without_waiting_for_the_timeout()
    {
        var (channel, _, firstBatch) = channelWithCapture(30.Seconds(), 100);

        await channel.PostAsync(1);
        channel.TriggerBatch();

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds(), TestContext.Current.CancellationToken));
        flushed.ShouldBe(firstBatch.Task);

        channel.Complete();
    }

    [Fact]
    public async Task trigger_batch_covers_every_item_posted_before_it_in_one_batch()
    {
        var (channel, batches, firstBatch) = channelWithCapture(30.Seconds(), 100);

        channel.Post(1);
        channel.Post(2);
        channel.Post(3);
        channel.TriggerBatch();

        await firstBatch.Task.WaitAsync(2.Seconds(), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        lock (batches)
        {
            batches.SelectMany(x => x).ShouldBe([1, 2, 3], "nothing lost, nothing duplicated, in order");
        }

        channel.Complete();
    }

    [Fact]
    public async Task an_item_posted_after_trigger_batch_keeps_its_own_batch_and_timer()
    {
        var (channel, batches, firstBatch) = channelWithCapture(30.Seconds(), 100);

        channel.Post(1);
        channel.TriggerBatch();
        await firstBatch.Task.WaitAsync(2.Seconds(), TestContext.Current.CancellationToken);

        channel.Post(2);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        lock (batches)
        {
            batches.ShouldHaveSingleItem().ShouldBe([1], "a later item is not flushed by an earlier trigger");
        }

        channel.TriggerBatch();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        lock (batches)
        {
            batches.Count.ShouldBe(2);
        }

        channel.Complete();
    }

    [Fact]
    public async Task trigger_batch_on_an_idle_channel_leaves_the_next_item_to_its_timer()
    {
        var (channel, batches, firstBatch) = channelWithCapture(300.Milliseconds(), 100);

        channel.TriggerBatch();
        channel.Post(1);

        await firstBatch.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

        lock (batches)
        {
            batches.ShouldHaveSingleItem().ShouldBe([1]);
        }

        channel.Complete();
    }
}
