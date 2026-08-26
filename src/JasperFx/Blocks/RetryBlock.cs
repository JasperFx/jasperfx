using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace JasperFx.Blocks;

public interface IItemHandler<T>
{
    Task ExecuteAsync(T message, CancellationToken cancellation);
}

public class LambdaItemHandler<T> : IItemHandler<T>
{
    private readonly Func<T, CancellationToken, Task> _handler;

    public LambdaItemHandler(Func<T, CancellationToken, Task> handler)
    {
        _handler = handler;
    }

    public Task ExecuteAsync(T message, CancellationToken cancellation)
    {
        return _handler(message, cancellation);
    }
}

public class RetryBlock<T> : IRetryBlock<T>, IDisposable
{
    private readonly Block<Item> _block;
    private readonly CancellationToken _cancellationToken;
    private readonly IItemHandler<T> _handler;
    private readonly ILogger _logger;

    public RetryBlock(Func<T, CancellationToken, Task> handler, ILogger logger, CancellationToken cancellationToken)
        : this(new LambdaItemHandler<T>(handler), logger, cancellationToken)
    {
    }

    public RetryBlock(IItemHandler<T> handler, ILogger logger, CancellationToken cancellationToken)
    {
        _handler = handler;
        _logger = logger;
        _cancellationToken = cancellationToken;

        // Unbounded: executeAsync re-posts failed items back onto this same block from within its own
        // processing action. With a bounded, back-pressuring block that self-re-enqueue would deadlock
        // against a full channel (GH-3287), so retries must never block on write.
        _block = new Block<Item>(1, Block<Item>.Unbounded, executeAsync);
    }

    public int MaximumAttempts { get; set; } = 3;
    public TimeSpan[] Pauses { get; set; } = [50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds()];

    /// <summary>
    /// Optional classification of a failure as transient (retry) or terminal (stop now). Consulted
    /// before every retry, including the first. Returning <c>false</c> ends the attempt sequence for
    /// that message immediately, no matter how many attempts are left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so a caller does not have to swallow an exception inside its own handler purely to stop
    /// the retry loop. Swallowing works, but it makes the give-up path indistinguishable from success
    /// at the block's boundary: the block never learns anything happened, so it cannot log it
    /// differently or hand it to <see cref="OnTerminalFailure"/>. See jasperfx#701.
    /// </para>
    /// <para>
    /// Null by default, which means "every failure is worth retrying" — the behavior of every
    /// existing block.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// new RetryBlock&lt;Envelope&gt;(settleAsync, logger, token)
    /// {
    ///     ShouldRetry = e => !AzureServiceBusSettlement.IsTerminal(e)
    /// };
    /// </code>
    /// </example>
    public Func<Exception, bool>? ShouldRetry { get; set; }

    /// <summary>
    /// Optional notification that <see cref="ShouldRetry"/> classified a failure as terminal and the
    /// message was abandoned. Awaited inline; exceptions thrown by the callback are logged and
    /// swallowed so a faulty hook cannot take down the block's processing loop.
    /// </summary>
    /// <remarks>
    /// This is the capability the swallow-in-the-callback pattern cannot provide: the block's owner
    /// gets to meter, dead-letter or otherwise account for a give-up that is not a success. Not
    /// invoked when a message is discarded for exhausting <see cref="MaximumAttempts"/> — that is a
    /// different outcome, and one the block has always logged on its own.
    /// </remarks>
    public Func<T, Exception, Task>? OnTerminalFailure { get; set; }

    public void Dispose()
    {
        _block.Complete();
    }

    public void Post(T message)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        var item = new Item(message);
        _block.Post(item);
    }

    public async Task PostAsync(T message)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        try
        {
            await _handler.ExecuteAsync(message, _cancellationToken);
        }
        catch (Exception e)
        {
            if (isTerminal(e))
            {
                await abandonAsync(message, e, 1).ConfigureAwait(false);
                return;
            }

            _logger.LogError(e, "Error while trying to retry {Item}", message);
            Post(message);
        }
    }

    private bool isTerminal(Exception e)
    {
        var shouldRetry = ShouldRetry;
        if (shouldRetry == null) return false;

        try
        {
            return !shouldRetry(e);
        }
        catch (Exception classificationFailure)
        {
            // A predicate that throws tells us nothing about the original failure, so fall back to
            // the pre-jasperfx#701 behavior of retrying rather than silently dropping the message.
            _logger.LogError(classificationFailure,
                "ShouldRetry threw while classifying a failure; treating the failure as transient");
            return false;
        }
    }

    private async Task abandonAsync(T message, Exception e, int attempts)
    {
        _logger.LogError(e,
            "Terminal failure for {Message} after {Attempts} attempt(s); no further attempts will be made",
            message, attempts);

        var onTerminalFailure = OnTerminalFailure;
        if (onTerminalFailure == null) return;

        try
        {
            await onTerminalFailure(message, e).ConfigureAwait(false);
        }
        catch (Exception callbackFailure)
        {
            _logger.LogError(callbackFailure, "Error in the OnTerminalFailure callback for {Message}", message);
        }
    }

    public TimeSpan DeterminePauseTime(int attempt)
    {
        if (attempt >= Pauses.Length)
        {
            return Pauses.LastOrDefault();
        }

        return Pauses[attempt - 1];
    }

    private async Task executeAsync(Item item, CancellationToken _)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        try
        {
            item.Attempts++;

            var pause = DeterminePauseTime(item.Attempts);
            await Task.Delay(pause, _cancellationToken);

            await _handler.ExecuteAsync(item.Message, _cancellationToken);

            _logger.LogDebug("Completed {Item}", item.Message);
        }
        catch (Exception e)
        {
            if (!_cancellationToken.IsCancellationRequested && isTerminal(e))
            {
                await abandonAsync(item.Message, e, item.Attempts).ConfigureAwait(false);
                return;
            }

            _logger.LogError(e, "Error while trying to retry {Item}", item.Message);

            if (_cancellationToken.IsCancellationRequested) return;

            if (item.Attempts < MaximumAttempts)
            {
                _block.Post(item);
            }
            else
            {
                _logger.LogInformation("Discarding message {Message} after {Attempts} attempts", item.Message,
                    item.Attempts);
            }
        }
    }

    public Task DrainAsync()
    {
        return _block.WaitForCompletionAsync();
    }

    public class Item
    {
        public Item(T item)
        {
            Message = item;
            Attempts = 0;
        }

        public int Attempts { get; set; }
        public T Message { get; }
    }
}