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

    // The downstream block is part of this chain: an item that has left the batching stage but is
    // still buffered downstream is work-not-yet-finished, and consumers of Count (back-pressure,
    // drain decisions) need to see it. Mirrors the same accounting fix in BlockSet.Count.
    public override uint Count => (uint)_current.Count + _inner.Count + _downstream.Count;

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

        // The trailing partial batch has to be drained under _syncLock, and the timer disarmed, or the
        // same items can be delivered TWICE on shutdown: this method used to read and post _current
        // unsynchronized and never clear it, while the flush timer armed by that batch's first item was
        // still live. A timer firing concurrently ran TriggerBatch, which posts AND clears, so both paths
        // shipped the same items. Reproduced as a duplicated trailing batch under a steady trickle
        // (BatchingChannelTests.steady_trickle_faster_than_the_timeout_still_flushes_within_the_max_age).
        // Disarming first also closes the window where the timer fires after this drain. A callback
        // already parked on the lock is harmless — whichever side wins clears _current, and the other
        // then sees it empty.
        T[]? trailing = null;
        lock (_syncLock)
        {
            disarmTimer();

            if (_current.Count > 0)
            {
                trailing = _current.ToArray();
                _current.Clear();
            }
        }

        // Deliberately outside the lock — _syncLock guards the buffer, never a downstream await.
        if (trailing != null)
        {
            await _downstream.PostAsync(trailing);
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