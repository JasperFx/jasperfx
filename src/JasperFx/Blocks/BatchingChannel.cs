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
                TriggerBatch();
            }
            catch (Exception)
            {
                // ignored
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public override uint Count => (uint)_current.Count + _inner.Count;

    public override Action<T, Exception> OnError
    {
        get => _inner.OnError;
        set => _inner.OnError = value;
    }

    public void TriggerBatch()
    {
        lock (_syncLock)
        {
            if (_current.Any())
            {
                _downstream.Post(_current.ToArray());
                _current.Clear();
            }

            disarmTimer();
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
                disarmTimer();
            }
            else if (_current.Count == 1)
            {
                // First item of a new batch: arm the one-shot flush timer. The timer is
                // deliberately NOT re-armed by later items — the timeout is the maximum age
                // of a batch, not a quiet-period debounce. The previous behavior (reset the
                // timer on every Post) meant a steady trickle arriving faster than the
                // timeout postponed the flush indefinitely until batchSize accumulated:
                // measured as multi-second p50 delivery latency at 8 msg/s with the default
                // 100/250ms settings in wolverine#3490.
                armTimer();
            }
        }
    }

    private void armTimer()
    {
        try
        {
            _trigger.Change(_timeOut, Timeout.InfiniteTimeSpan);
        }
        catch (Exception)
        {
            // ignored — the timer may already be disposed during shutdown
        }
    }

    private void disarmTimer()
    {
        try
        {
            _trigger.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception)
        {
            // ignored — the timer may already be disposed during shutdown
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

        await _downstream.WaitForCompletionAsync();
    }

    public override void Complete()
    {
        _inner.Complete();
    }

    public override ValueTask PostAsync(T item)
    {
        return _inner.PostAsync(item);
    }

    public override void Post(T item)
    {
        _inner.Post(item);
    }
}