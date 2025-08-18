namespace JasperFx.Blocks;

/// <summary>
/// Batches or buffers individual items coming into this channel and publishes those batches to
/// a downstream IBlock
/// </summary>
/// <typeparam name="T"></typeparam>
public class BatchingChannel<T> : BlockBase<T>
{
    private readonly TimeSpan _timeOut;
    private readonly IBlock<T[]> _downstream;
    private readonly int _batchSize;
    private readonly List<T> _current;
    private readonly object _syncLock = new();
    private readonly Block<T> _inner;
    private readonly Timer _trigger;

    public BatchingChannel(TimeSpan timeOut, IBlock<T[]> downstream, int batchSize = 100)
    {
        _current = new List<T>(batchSize);
        _timeOut = timeOut;
        _downstream = downstream;
        _batchSize = batchSize;

        _inner = new Block<T>(addItem);
        
        _trigger = new Timer(_ =>
        {
            try
            {
                triggerBatch();
            }
            catch (Exception)
            {
                // ignored
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void triggerBatch()
    {
        lock (_syncLock)
        {
            if (_current.Any())
            {
                _downstream.Post(_current.ToArray());
                _current.Clear();
            }
        }
    }

    private void addItem(T item)
    {
        lock (_syncLock)
        {
            _current.Add(item);
            if (_current.Count >= _batchSize)
            {
                _downstream.Post(_current.ToArray());
                _current.Clear();
            }
        }
    }


    public override ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    public override async Task WaitForCompletionAsync()
    {
        await _inner.WaitForCompletionAsync();
        // ReSharper disable once InconsistentlySynchronizedField
        if (_current.Any())
        {
            // ReSharper disable once InconsistentlySynchronizedField
            await _downstream.PostAsync(_current.ToArray());
        }

        // Don't wait for downstream because we'll do that through 
    }

    public override void Complete()
    {
        _inner.Complete();
    }

    public override ValueTask PostAsync(T item)
    {
        try
        {
            _trigger.Change(_timeOut, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // ignored
        }

        return _inner.PostAsync(item);
    }

    public override void Post(T item)
    {
        try
        {
            _trigger.Change(_timeOut, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // ignored
        }

        _inner.Post(item);
    }
}