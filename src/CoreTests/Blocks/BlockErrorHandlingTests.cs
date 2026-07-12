using JasperFx.Blocks;
using Shouldly;
using Xunit;

namespace CoreTests.Blocks;

// jasperfx#506: exceptions escaping a block's action must be observable, the error callback must
// never be able to kill the consumer loop, and a dead (faulted) block must be observable by its
// producers instead of silently accepting items forever
public class BlockErrorHandlingTests
{
    [Fact]
    public async Task on_error_receives_the_item_and_exception_and_the_loop_survives()
    {
        var failures = new List<(string, Exception)>();
        var processed = new List<string>();
        var secondItemProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = new Block<string>(async (item, _) =>
        {
            if (item == "poison")
            {
                throw new DivideByZeroException("boom");
            }

            processed.Add(item);
            if (item == "second")
            {
                secondItemProcessed.SetResult();
            }

            await Task.CompletedTask;
        });

        block.OnError = (item, ex) => failures.Add((item, ex));

        block.Post("first");
        block.Post("poison");
        block.Post("second");

        await secondItemProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        processed.ShouldBe(new[] { "first", "second" });
        failures.Count.ShouldBe(1);
        failures[0].Item1.ShouldBe("poison");
        failures[0].Item2.ShouldBeOfType<DivideByZeroException>();
        block.Failure.ShouldBeNull();

        await block.WaitForCompletionAsync();
    }

    [Fact]
    public async Task a_throwing_error_callback_faults_the_block_and_post_throws()
    {
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = new Block<string>((item, _) => throw new DivideByZeroException("action"));

        var callbackInvocations = 0;
        block.OnError = (_, _) =>
        {
            // Only throw on the per-item invocation. The fault path re-invokes the callback with
            // the terminal exception; letting that second call succeed lets the test observe it
            if (Interlocked.Increment(ref callbackInvocations) == 1)
            {
                throw new InvalidCastException("error handling is broken too");
            }

            faulted.SetResult();
        };

        block.Post("poison");

        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        block.Failure.ShouldNotBeNull();
        var aggregate = block.Failure.ShouldBeOfType<AggregateException>();
        aggregate.InnerExceptions.OfType<DivideByZeroException>().Any().ShouldBeTrue();
        aggregate.InnerExceptions.OfType<InvalidCastException>().Any().ShouldBeTrue();

        // A dead consumer must be observable by producers (jasperfx#506) -- the old behavior was
        // for Post/PostAsync to keep succeeding forever against a consumer that no longer exists
        var thrown = Should.Throw<InvalidOperationException>(() => block.Post("after the fault"));
        thrown.InnerException.ShouldBeSameAs(block.Failure);

        await Should.ThrowAsync<InvalidOperationException>(async () => await block.PostAsync("also after"));
    }

    [Fact]
    public async Task fault_releases_a_producer_blocked_on_a_full_bounded_channel()
    {
        var letTheActionFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Capacity 1: one item in flight blocking the consumer, one buffered, so the next
        // PostAsync waits for capacity
        var block = new Block<string>(1, 1, async (item, _) =>
        {
            actionStarted.TrySetResult();
            await letTheActionFinish.Task;
            if (item == "poison")
            {
                throw new DivideByZeroException();
            }
        });

        block.OnError = (_, _) => throw new InvalidCastException("kill the block");

        block.Post("poison");
        await actionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        block.Post("buffered");

        // This producer has no capacity left and parks waiting for the consumer
        var blockedProducer = Task.Run(async () => await block.PostAsync("parked"));
        blockedProducer.IsCompleted.ShouldBeFalse();

        // Now the action throws, the error callback throws, and the block faults. The parked
        // producer must be released with an exception, not left hanging forever against a dead
        // consumer (the bounded-channel hang scenario from jasperfx#506)
        letTheActionFinish.SetResult();

        await Should.ThrowAsync<Exception>(async () =>
            await blockedProducer.WaitAsync(TimeSpan.FromSeconds(5)));

        block.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task wait_for_completion_on_a_faulted_block_returns_quietly()
    {
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = new Block<string>((_, _) => throw new DivideByZeroException());
        var invocations = 0;
        block.OnError = (_, _) =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                throw new InvalidCastException();
            }

            faulted.SetResult();
        };

        block.Post("poison");
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Teardown paths call this on shutdown; a fault must not blow up the shutdown itself
        await block.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task default_error_sink_is_not_compiled_away_in_release_builds()
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);

        try
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = new Block<string>((item, _) =>
            {
                if (item == "poison")
                {
                    throw new DivideByZeroException("visible in production");
                }

                processed.SetResult();
                return Task.CompletedTask;
            });

            // Deliberately NOT setting OnError -- this exercises the default sink, which used to
            // be Debug.WriteLine and therefore /dev/null in release builds (jasperfx#506)
            block.Post("poison");
            block.Post("after");

            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await block.WaitForCompletionAsync();

            writer.ToString().ShouldContain("DivideByZeroException");
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
