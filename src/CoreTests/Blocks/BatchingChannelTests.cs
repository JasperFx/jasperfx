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
                await Task.Delay(25);
            }
        });

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds()));
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
    public async Task lone_item_flushes_after_the_timeout()
    {
        var (channel, batches, firstBatch) = channelWithCapture(100.Milliseconds(), 100);

        channel.Post(42);

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds()));
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

        var flushed = await Task.WhenAny(firstBatch.Task, Task.Delay(2.Seconds()));
        flushed.ShouldBe(firstBatch.Task, "a full batch should flush without waiting on the timer");

        channel.Complete();
        await channel.WaitForCompletionAsync();

        lock (batches)
        {
            batches.SelectMany(x => x).OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));
        }
    }
}
