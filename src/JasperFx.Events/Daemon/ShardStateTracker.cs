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
        _block.OnError = (state, ex) =>
            _logger.LogError(ex, "Failure while publishing shard state {State}", state);

        _subscription = Subscribe(this);
    }

    /// <summary>
    ///     Currently known "high water mark" denoting the highest complete sequence
    ///     of the event storage
    /// </summary>
    public long HighWaterMark { get; private set; }

    /// <summary>
    ///     <see cref="IEventDatabase.Identifier" /> of the database this tracker belongs to. Stamped onto every
    ///     published <see cref="ShardState" /> that doesn't already carry one, so a consumer of a multi-database
    ///     store (database-per-tenant, sharded tenancy) can tell one database's <c>Trip:All</c> from another's.
    ///     Set by <see cref="JasperFxAsyncDaemon{TOperations,TQuerySession,TProjection}" /> at construction. See
    ///     JasperFx/CritterWatch#678.
    /// </summary>
    public string? DatabaseIdentifier { get; set; }

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

    public ValueTask PublishAsync(ShardState state)
    {
        if (state.ShardName == ShardState.HighWaterMark)
        {
            HighWaterMark = state.Sequence;
        }

        // One stamp point for every state this database's daemon publishes — the subscription agents'
        // progress and the high water agent's marks alike. A state that already names a database (a
        // republish, or a publisher that knows better) keeps its own.
        state.DatabaseIdentifier ??= DatabaseIdentifier;

        return _block.PostAsync(state);
    }

    public ValueTask MarkHighWaterAsync(long sequence)
    {
        return MarkHighWaterAsync(sequence, null);
    }

    /// <summary>
    /// Publish a new high water mark and stamp the moment that mark was observed so a daemon
    /// consumer / ShardState reader can compute "seconds since the high water mark last advanced"
    /// authoritatively, rather than falling back to a client-side heuristic that resets on reconnect.
    /// A null <paramref name="lastAdvanced" /> leaves <see cref="ShardState.LastAdvanced" /> unset
    /// (the pre-jasperfx#449 behavior). See jasperfx#449.
    /// </summary>
    public ValueTask MarkHighWaterAsync(long sequence, DateTimeOffset? lastAdvanced)
    {
        return PublishAsync(new ShardState(ShardState.HighWaterMark, sequence)
        {
            LastAdvanced = lastAdvanced
        });
    }

    public ValueTask MarkSkippingAsync(long lastKnownGoodHighWaterMark, long newHighWaterMark,
        DateTimeOffset? lastAdvanced = null)
    {
        var skipped = newHighWaterMark - lastKnownGoodHighWaterMark;
        return PublishAsync(new ShardState(ShardState.HighWaterMark, newHighWaterMark)
        {
            PreviousGoodMark = lastKnownGoodHighWaterMark,
            Action = ShardAction.Skipped,
            SkippedEventsCount = skipped > 0 ? skipped : null,
            LastAdvanced = lastAdvanced
        });
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
