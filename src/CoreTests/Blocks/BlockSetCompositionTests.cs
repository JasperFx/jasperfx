using JasperFx.Blocks;
using Shouldly;

namespace CoreTests.Blocks;

/// <summary>
/// A BlockSet produced by PushUpstream must account for the WHOLE chain. Wolverine reads
/// BufferedReceiver.QueueCount straight off this Count to decide back-pressure latching and,
/// critically, when to RESUME a latched listener — a Count that misses the downstream backlog
/// (or double-counts the top stage) makes those decisions against the wrong number.
/// </summary>
public class BlockSetCompositionTests
{
    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task count_reflects_downstream_backlog()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;

        await using var downstream = new Block<string>(async (_, _) =>
        {
            await gate.Task;
            Interlocked.Increment(ref processed);
        });

        var set = downstream.PushUpstream<int>(i => i.ToString());

        for (var i = 0; i < 10; i++)
        {
            set.Post(i);
        }

        // Let the transform stage push everything into the (blocked) downstream block
        await WaitUntil(() => downstream.Count == 10, "downstream block holds all 10 items");

        // The backlog is sitting in the downstream block; the set must report it
        set.Count.ShouldBe(10u);

        gate.SetResult();
        await WaitUntil(() => set.Count == 0, "chain fully drained");
        processed.ShouldBe(10);
    }

    [Fact]
    public async Task count_does_not_double_count_the_top_stage()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var downstream = new Block<string>((_, _) => Task.CompletedTask);

        var set = downstream.PushUpstream<int>(async (i, _) =>
        {
            await gate.Task;
            return i.ToString();
        });

        for (var i = 0; i < 10; i++)
        {
            set.Post(i);
        }

        // All 10 items are held in the (blocked) top transform stage. The set holds exactly
        // 10 items total — not 20.
        set.Count.ShouldBe(10u);

        gate.SetResult();
        await WaitUntil(() => set.Count == 0, "chain fully drained");
    }

    [Fact]
    public async Task wait_for_completion_drains_the_downstream_block_too()
    {
        var processed = 0;

        await using var downstream = new Block<string>(async (_, _) =>
        {
            await Task.Delay(30);
            Interlocked.Increment(ref processed);
        });

        var set = downstream.PushUpstream<int>(i => i.ToString());

        for (var i = 0; i < 10; i++)
        {
            await set.PostAsync(i);
        }

        await set.WaitForCompletionAsync();

        // Completion of the set means the whole chain finished, not just the top stage
        processed.ShouldBe(10);
    }
}
