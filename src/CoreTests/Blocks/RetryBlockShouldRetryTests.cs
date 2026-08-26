using JasperFx.Blocks;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Blocks;

/// <summary>
/// <see cref="RetryBlock{T}.ShouldRetry"/> and <see cref="RetryBlock{T}.OnTerminalFailure"/> —
/// classifying a failure as terminal instead of swallowing it inside the callback to stop the loop
/// (jasperfx#701).
/// </summary>
/// <remarks>
/// Every test here is driven by a deterministic signal — a <see cref="TaskCompletionSource"/> the
/// handler completes, or the completion of <c>PostAsync</c> itself — rather than by polling for a log
/// line. The pauses are zeroed so nothing waits on the retry schedule.
/// </remarks>
public class RetryBlockShouldRetryTests
{
    private readonly SpyLogger theLogger = new();

    private static readonly TimeSpan[] NoPauses = [0.Milliseconds()];

    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_terminal_failure_ends_the_attempt_sequence_immediately()
    {
        var attempts = 0;
        var abandoned = new TaskCompletionSource<Exception>();
        var failure = new InvalidOperationException("PRECONDITION_FAILED - unknown delivery tag");

        using var block = new RetryBlock<string>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw failure;
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 5,
            ShouldRetry = _ => false,
            OnTerminalFailure = (_, e) =>
            {
                abandoned.SetResult(e);
                return Task.CompletedTask;
            }
        };

        block.Post("settle-me");

        var caught = await abandoned.Task.WaitAsync(10.Seconds(), Cancellation);

        caught.ShouldBeSameAs(failure);

        // The point of the feature: one attempt, not MaximumAttempts of them. The RabbitMQ needle in
        // wolverine#4012 burned three pointless retries for exactly this case.
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task the_terminal_callback_receives_the_message_and_the_exception()
    {
        var failure = new InvalidOperationException("no such receipt handle");
        string? abandonedMessage = null;
        Exception? abandonedException = null;

        using var block = new RetryBlock<string>((_, _) => throw failure, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            ShouldRetry = _ => false,
            OnTerminalFailure = (message, e) =>
            {
                abandonedMessage = message;
                abandonedException = e;
                return Task.CompletedTask;
            }
        };

        await block.PostAsync("settle-me");

        abandonedMessage.ShouldBe("settle-me");
        abandonedException.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task post_async_does_not_re_enqueue_a_terminal_failure()
    {
        var attempts = 0;
        var terminalFailures = 0;

        using var block = new RetryBlock<string>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("terminal");
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 5,
            ShouldRetry = _ => false,
            OnTerminalFailure = (_, _) =>
            {
                Interlocked.Increment(ref terminalFailures);
                return Task.CompletedTask;
            }
        };

        // PostAsync tries inline first and re-posts to the block on failure. A terminal classification
        // has to stop it there, so by the time this await returns the message is done for good.
        await block.PostAsync("settle-me");

        attempts.ShouldBe(1);
        terminalFailures.ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_classified_as_transient_still_retries()
    {
        var attempts = 0;
        var succeeded = new TaskCompletionSource();

        using var block = new RetryBlock<string>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                throw new InvalidOperationException("transient");
            }

            succeeded.SetResult();
            return Task.CompletedTask;
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 5,
            ShouldRetry = _ => true
        };

        block.Post("settle-me");

        await succeeded.Task.WaitAsync(10.Seconds(), Cancellation);

        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task exhausting_the_attempts_is_not_a_terminal_failure()
    {
        var attempts = 0;
        var terminalFailures = 0;
        var exhausted = new TaskCompletionSource();

        using var block = new RetryBlock<string>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 3)
            {
                exhausted.SetResult();
            }

            throw new InvalidOperationException("always fails");
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 3,
            ShouldRetry = _ => true,
            OnTerminalFailure = (_, _) =>
            {
                Interlocked.Increment(ref terminalFailures);
                return Task.CompletedTask;
            }
        };

        block.Post("settle-me");

        await exhausted.Task.WaitAsync(10.Seconds(), Cancellation);

        // Running out of attempts and being classified terminal are different outcomes. The block has
        // always logged the first one on its own; only the second reaches the callback.
        terminalFailures.ShouldBe(0);
    }

    [Fact]
    public async Task a_should_retry_predicate_that_throws_is_treated_as_transient()
    {
        var attempts = 0;
        var succeeded = new TaskCompletionSource();

        using var block = new RetryBlock<string>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) < 2)
            {
                throw new InvalidOperationException("transient");
            }

            succeeded.SetResult();
            return Task.CompletedTask;
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 5,
            ShouldRetry = _ => throw new DivideByZeroException("a broken classifier")
        };

        block.Post("settle-me");

        await succeeded.Task.WaitAsync(10.Seconds(), Cancellation);

        // A classifier that blew up tells us nothing about the original failure, so the block falls
        // back to retrying rather than silently dropping the message.
        attempts.ShouldBe(2);
        theLogger.Exceptions.ShouldContain(x => x is DivideByZeroException);
    }

    [Fact]
    public async Task a_throwing_terminal_callback_is_logged_and_swallowed()
    {
        using var block = new RetryBlock<string>((_, _) => throw new InvalidOperationException("terminal"),
            theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            ShouldRetry = _ => false,
            OnTerminalFailure = (_, _) => throw new DivideByZeroException("a broken hook")
        };

        // A faulty hook must not escape into the caller, and must not take down the block's loop.
        await block.PostAsync("settle-me");

        theLogger.Exceptions.ShouldContain(x => x is DivideByZeroException);
    }

    [Fact]
    public async Task no_predicate_leaves_the_pre_existing_behavior_alone()
    {
        var attempts = 0;
        var exhausted = new TaskCompletionSource();

        using var block = new RetryBlock<string>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 3)
            {
                exhausted.SetResult();
            }

            throw new InvalidOperationException("always fails");
        }, theLogger, CancellationToken.None)
        {
            Pauses = NoPauses,
            MaximumAttempts = 3
        };

        block.Post("settle-me");

        await exhausted.Task.WaitAsync(10.Seconds(), Cancellation);

        attempts.ShouldBe(3);
    }
}
