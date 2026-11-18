using JasperFx.Blocks;
using Shouldly;
using Xunit;

namespace CoreTests.Blocks;

// jasperfx#1231: a post that passed the "already completed" check and was still writing when Complete() landed threw
// "The channel has been closed" at its producer, where a post one moment later was dropped quietly. A full bounded
// channel holds the write open, so Complete() lands in the middle of it every time.
public class BlockPostRacingCompleteTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task an_async_post_waiting_for_room_when_the_block_completes_is_dropped_quietly()
    {
        await using var harness = new FullBlock();
        await harness.FillAsync();

        var post = harness.Block.PostAsync("c");
        post.IsCompleted.ShouldBeFalse();

        harness.Block.Complete();

        await Should.NotThrowAsync(async () => await post.AsTask().WaitAsync(Timeout));
        harness.Errors.ShouldBeEmpty();

        await harness.DrainAsync();
        harness.Processed.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_sync_post_waiting_for_room_when_the_block_completes_is_dropped_without_an_error()
    {
        await using var harness = new FullBlock();
        await harness.FillAsync();

        var post = Task.Run(() => harness.Block.Post("c"), TestContext.Current.CancellationToken);

        // Counted means it got past the "already completed" check, so it is writing or about to
        while (harness.Block.Count < 3)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        harness.Block.Complete();

        await post.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        harness.Errors.ShouldBeEmpty();

        await harness.DrainAsync();
        harness.Processed.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_post_waiting_for_room_when_the_block_faults_still_sees_the_fault()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = new Block<string>(1, 1, async (item, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            throw new DivideByZeroException();
        });

        // A throwing error callback faults the block (jasperfx#506)
        block.OnError = (_, _) => throw new InvalidOperationException("broken error handling");

        block.Post("a");
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        block.Post("b");

        var post = block.PostAsync("c");
        post.IsCompleted.ShouldBeFalse();

        gate.SetResult();

        await Should.ThrowAsync<Exception>(async () => await post.AsTask().WaitAsync(Timeout));
        block.Failure.ShouldNotBeNull();
    }

    // Capacity one and an action held on a gate: "a" is in the action, "b" fills the channel, so the next write waits
    private sealed class FullBlock : IAsyncDisposable
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FullBlock()
        {
            Block = new Block<string>(1, 1, async (item, _) =>
            {
                _started.TrySetResult();
                await _gate.Task;
                lock (Processed) Processed.Add(item);
            });

            Block.OnError = (item, ex) =>
            {
                lock (Errors) Errors.Add((item, ex));
            };
        }

        public Block<string> Block { get; }
        public List<string> Processed { get; } = new();
        public List<(string, Exception)> Errors { get; } = new();

        public async Task FillAsync()
        {
            Block.Post("a");
            await _started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Block.Post("b");
        }

        public async Task DrainAsync()
        {
            _gate.TrySetResult();
            await Block.WaitForCompletionAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _gate.TrySetResult();
            await Block.DisposeAsync();
        }
    }
}
