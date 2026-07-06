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
            _logger.LogError(e, "Error while trying to retry {Item}", message);
            Post(message);
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