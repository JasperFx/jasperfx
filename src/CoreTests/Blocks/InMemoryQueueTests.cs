using System.Collections.Concurrent;
using JasperFx.Blocks;
using Shouldly;

namespace CoreTests.Blocks;

public class InMemoryQueueTests
{
    private List<Guid> randomList(int count)
    {
        var list = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            list.Add(Guid.NewGuid());
        }

        return list;
    }

    [Fact]
    public async Task process_a_bunch()
    {
        var list = new List<Guid>();
        var expected = randomList(100);

        await using var queue = new Block<Guid>(async (n, _) =>
        {
            await Task.Delay(Random.Shared.Next(50, 100), _);
            list.Add(n);
        });

        foreach (var guid in expected)
        {
            await queue.PostAsync(guid);
        }
        
        await queue.WaitForCompletionAsync();
        
        list.ShouldBe(expected);
    }
    
    [Fact]
    public async Task process_with_parallel_writers()
    {
        var expected1 = randomList(100);
        var expected2 = randomList(100);
        var expected3 = randomList(100);
        var expected4 = randomList(100);
        
        var list = new List<Guid>();
        
        await using var queue = new Block<Guid>(async (n, _) =>
        {
            await Task.Delay(Random.Shared.Next(10, 25), _);
            list.Add(n);
        });

        Func<List<Guid>, Task> publish = async l =>
        {
            foreach (var guid in l)
            {
                await queue.PostAsync(guid);
            }
        };
        
        await Task.WhenAll([
            Task.Run(() => publish(expected1)),
            Task.Run(() => publish(expected2)),
            Task.Run(() => publish(expected3)),
            Task.Run(() => publish(expected4)),
            
            
            ]);
        
        await queue.WaitForCompletionAsync();

        var all = expected1.Concat(expected2).Concat(expected3).Concat(expected4).OrderBy(x => x).ToArray();
        var actual = list.OrderBy(x => x).ToArray();
        
        actual.ShouldBe(all);
    }
    
        
    [Fact]
    public async Task process_with_parallel_writers_and_parallel_readers()
    {
        var expected1 = randomList(100);
        var expected2 = randomList(100);
        var expected3 = randomList(100);
        var expected4 = randomList(100);
        
        var list = new ConcurrentBag<Guid>();
        
        await using var queue = new Block<Guid>(10, async (n, _) =>
        {
            await Task.Delay(Random.Shared.Next(10, 25), _);
            list.Add(n);
        });

        Func<List<Guid>, Task> publish = async l =>
        {
            foreach (var guid in l)
            {
                await queue.PostAsync(guid);
            }
        };
        
        await Task.WhenAll([
            Task.Run(() => publish(expected1)),
            Task.Run(() => publish(expected2)),
            Task.Run(() => publish(expected3)),
            Task.Run(() => publish(expected4)),
            
            
        ]);
        
        await queue.WaitForCompletionAsync();

        var all = expected1.Concat(expected2).Concat(expected3).Concat(expected4).OrderBy(x => x).ToArray();
        var actual = list.OrderBy(x => x).ToArray();
        
        actual.Length.ShouldBe(all.Length);

        //actual.ShouldBe(all);
    }

    [Fact]
    public async Task synchronous_Post_does_not_drop_items_when_the_bounded_channel_fills()
    {
        // Regression for GH-3287: previously Post() used a non-blocking TryWrite and silently dropped
        // every item once more than the bounded capacity (10k) were queued faster than the reader drained.
        // Here the reader is held closed until the channel has saturated and the producer is blocked in
        // Post() waiting for capacity -- proving the item is back-pressured rather than dropped.
        const int count = 25_000;
        var processed = 0;

        // The reader is held on an ASYNC gate, not a synchronous ManualResetEventSlim.Wait. The channel is
        // built with AllowSynchronousContinuations, so the producer's first TryWrite can run the reader's
        // continuation inline on the producer's own thread; with a blocking gate that hijacked the producer
        // into the wait and it posted a single item before parking forever (Count==1 flake, net10). An
        // awaited TCS yields the thread back instead, so the producer keeps filling regardless of which
        // thread the continuation lands on.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new Block<int>(1, Block<int>.DefaultBoundedCapacity, async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            Interlocked.Increment(ref processed);
        });

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
            {
                queue.Post(i);
            }
        });

        // Wait until the channel is saturated (producer is now blocked inside Post waiting for room).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (queue.Count < Block<int>.DefaultBoundedCapacity && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        queue.Count.ShouldBeGreaterThanOrEqualTo((uint)Block<int>.DefaultBoundedCapacity);
        producer.IsCompleted.ShouldBeFalse(); // still blocked -> the overflow item was NOT dropped

        gate.SetResult();

        await producer;
        await queue.WaitForCompletionAsync();

        processed.ShouldBe(count);
    }

    [Fact]
    public async Task unbounded_block_never_blocks_or_drops_on_Post()
    {
        const int count = 25_000;
        var processed = 0;

        await using var queue = new Block<int>(1, Block<int>.Unbounded, (_, _) =>
        {
            Interlocked.Increment(ref processed);
            return Task.CompletedTask;
        });

        for (var i = 0; i < count; i++)
        {
            queue.Post(i);
        }

        await queue.WaitForCompletionAsync();

        processed.ShouldBe(count);
    }
}
