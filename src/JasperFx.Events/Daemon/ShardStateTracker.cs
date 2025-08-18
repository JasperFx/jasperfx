using System.Collections.Immutable;
using ImTools;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Observable for progress and action updates for all running asynchronous projection shards
/// </summary>
public class ShardStateTracker: IObservable<ShardState>, IObserver<ShardState>, IDisposable
{
    private readonly Block<ShardState> _block;
    private readonly ILogger _logger;
    private readonly IDisposable _subscription;
    private ImmutableList<IObserver<ShardState>> _listeners = ImmutableList<IObserver<ShardState>>.Empty;
    private ImHashMap<string, ShardState> _states = ImHashMap<string, ShardState>.Empty;

    public ShardStateTracker(ILogger logger)
    {
        _logger = logger;
        _block = new Block<ShardState>(publish);

        _subscription = Subscribe(this);
    }

    /// <summary>
    ///     Currently known "high water mark" denoting the highest complete sequence
    ///     of the event storage
    /// </summary>
    public long HighWaterMark { get; private set; }

    void IDisposable.Dispose()
    {
        _subscription.Dispose();
        _block.Complete();
    }

    /// <summary>
    ///     Register a new observer of projection shard events. The return disposable
    ///     can be used to unsubscribe the observer from the tracker
    /// </summary>
    /// <param name="observer"></param>
    /// <returns></returns>
    public IDisposable Subscribe(IObserver<ShardState> observer)
    {
        if (!_listeners.Contains(observer))
        {
            _listeners = _listeners.Add(observer);
        }

        return new Unsubscriber(this, observer);
    }

    void IObserver<ShardState>.OnCompleted()
    {
    }

    void IObserver<ShardState>.OnError(Exception error)
    {
    }

    void IObserver<ShardState>.OnNext(ShardState value)
    {
        _states = _states.AddOrUpdate(value.ShardName, value);
    }

    [Obsolete("Try to eliminate this")]
    public void Publish(ShardState state)
    {
        if (state.ShardName == ShardState.HighWaterMark)
        {
            HighWaterMark = state.Sequence;
        }

        _block.Post(state);
    }
    
    public ValueTask PublishAsync(ShardState state)
    {
        if (state.ShardName == ShardState.HighWaterMark)
        {
            HighWaterMark = state.Sequence;
        }

        return _block.PostAsync(state);
    }

    public ValueTask MarkHighWaterAsync(long sequence)
    {
        return PublishAsync(new ShardState(ShardState.HighWaterMark, sequence));
    }
    
    public ValueTask MarkSkippingAsync(long lastKnownGoodHighWaterMark, long newHighWaterMark)
    {
        return PublishAsync(new ShardState(ShardState.HighWaterMark, newHighWaterMark){PreviousGoodMark = lastKnownGoodHighWaterMark, Action = ShardAction.Skipped});
    }

    /// <summary>
    ///     Use to "wait" for an expected projection shard state
    /// </summary>
    /// <param name="expected"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public Task<ShardState> WaitForShardState(ShardState expected, TimeSpan? timeout = null)
    {
        if (_states.TryFind(expected.ShardName, out var state))
        {
            if (state.Equals(expected) || state.Sequence >= expected.Sequence)
            {
                return Task.FromResult(state);
            }
        }

        timeout ??= 1.Minutes();
        var listener = new ShardStatusWatcher(this, expected, timeout.Value);
        return listener.Task;
    }

    /// <summary>
    ///     Use to "wait" for an expected projection shard state
    /// </summary>
    /// <param name="shardName"></param>
    /// <param name="sequence"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public Task<ShardState> WaitForShardState(string shardName, long sequence, TimeSpan? timeout = null)
    {
        return WaitForShardState(new ShardState(shardName, sequence), timeout);
    }

    /// <summary>
    ///     Use to "wait" for an expected projection shard state
    /// </summary>
    /// <param name="name"></param>
    /// <param name="sequence"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public Task<ShardState> WaitForShardState(ShardName name, long sequence, TimeSpan? timeout = null)
    {
        if (_states.TryFind(name.Identity, out var state))
        {
            if (state.Sequence >= sequence)
            {
                return Task.FromResult(state);
            }
        }

        return WaitForShardState(new ShardState(name.Identity, sequence), timeout);
    }

    /// <summary>
    ///     Use to "wait" for an expected projection shard condition
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="description"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public Task<ShardState> WaitForShardCondition(Func<ShardState, bool> condition, string description,
        TimeSpan? timeout = null)
    {
        timeout ??= 1.Minutes();
        var listener = new ShardStatusWatcher(description, condition, this, timeout.Value);
        return listener.Task;
    }

    /// <summary>
    ///     Wait for the high water mark to attain the given sequence number
    /// </summary>
    /// <param name="sequence"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public Task<ShardState> WaitForHighWaterMark(long sequence, TimeSpan? timeout = null)
    {
        return HighWaterMark >= sequence
            ? Task.FromResult(new ShardState(ShardState.HighWaterMark, HighWaterMark))
            : WaitForShardState(ShardState.HighWaterMark, sequence, timeout);
    }

    public Task Complete()
    {
        _block.Complete();
        return _block.WaitForCompletionAsync();
    }

    private void publish(ShardState state)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received {ShardState}", state);
        }

        foreach (var observer in _listeners)
        {
            try
            {
                observer.OnNext(state);
            }
            catch (Exception e)
            {
                // log them, but never let it through
                _logger.LogError(e, "Failed to notify subscriber {Subscriber} of shard state {ShardState}", observer,
                    state);
            }
        }
    }

    public void Finish()
    {
        foreach (var observer in _listeners)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    public void MarkAsRestarted(ShardName name)
    {
        _states = _states.Remove(name.Identity);
    }

    private class Unsubscriber: IDisposable
    {
        private readonly IObserver<ShardState> _observer;
        private readonly ShardStateTracker _tracker;

        public Unsubscriber(ShardStateTracker tracker, IObserver<ShardState> observer)
        {
            _tracker = tracker;
            _observer = observer;
        }

        public void Dispose()
        {
            if (_observer != null)
            {
                _tracker._listeners = _tracker._listeners.Remove(_observer);
            }
        }
    }

}
