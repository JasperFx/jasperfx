using System.Diagnostics;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Used mostly by tests to listen for expected shard events or progress
/// </summary>
internal class ShardStatusWatcher : IObserver<ShardState>
{
    private readonly TaskCompletionSource<ShardState> _completion;
    private readonly Func<ShardState, bool> _condition;
    private readonly CancellationTokenSource _timeout;
    private readonly IDisposable _unsubscribe;

    public ShardStatusWatcher(ShardStateTracker tracker, ShardState expected, TimeSpan timeout)
        : this(tracker, x => x.ShardName == expected.ShardName && x.Sequence >= expected.Sequence, timeout,
            $"Shard {expected.ShardName} did not reach sequence number {expected.Sequence} in the time allowed")
    {
    }

    public ShardStatusWatcher(string description, Func<ShardState, bool> condition, ShardStateTracker tracker,
        TimeSpan timeout)
        : this(tracker, condition, timeout, $"{description} was not detected in the time allowed")
    {
        Debug.WriteLine("Subscribed to watch shard state: " + description);
    }

    private ShardStatusWatcher(ShardStateTracker tracker, Func<ShardState, bool> condition, TimeSpan timeout,
        string timeoutMessage)
    {
        _condition = condition;
        _completion = new TaskCompletionSource<ShardState>(TaskCreationOptions.RunContinuationsAsynchronously);

        _timeout = new CancellationTokenSource(timeout);
        _timeout.Token.Register(() =>
        {
            if (_completion.TrySetException(new TimeoutException(timeoutMessage)))
            {
                // ReSharper disable once ConstantConditionalAccessQualifier -- the token can fire before
                // the constructor has finished assigning _unsubscribe if the timeout is tiny.
                _unsubscribe?.Dispose();
            }
        });

        // jasperfx#568: publication is asynchronous — PublishAsync posts to a Block and returns, and the
        // consumer thread walks the listener list that existed when it started. A state can therefore be
        // delivered (and recorded in the tracker's state map) in the window between a caller checking the
        // map and this watcher subscribing, and nothing else may ever be published for that shard. Since
        // OnNext only ever sees states delivered AFTER this point, re-check what the tracker already knows
        // now that we're subscribed. TrySetResult makes a double hit harmless — whichever path wins first,
        // the other is a no-op. This is also the only current-state check WaitForShardCondition gets.
        //
        // jasperfx#572: subscribing and then reading the snapshot as two separate steps still left a hole
        // for a watcher to fall through — the publication walk could capture the listener list without this
        // watcher, and the snapshot could still be read before that state was recorded, so NEITHER path saw
        // it. Taking both in one atomic step against the tracker's publication lock closes it rather than
        // narrowing it.
        _unsubscribe = tracker.SubscribeAndCaptureCurrentStates(this, out var currentStates);

        foreach (var state in currentStates)
        {
            try
            {
                if (check(state))
                {
                    break;
                }
            }
            catch (Exception)
            {
                // A user supplied condition that blows up on some unrelated shard's state has always
                // just meant "no match" -- ShardStateTracker.publish() swallows and logs whatever a
                // listener throws. Keep that contract here instead of throwing out of WaitFor*.
            }
        }
    }

    public Task<ShardState> Task => _completion.Task;

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
        if (_completion.TrySetException(error))
        {
            _unsubscribe.Dispose();
            _timeout.Dispose();
        }
    }

    public void OnNext(ShardState value)
    {
        check(value);
    }

    private bool check(ShardState state)
    {
        if (!_condition(state)) return false;

        if (_completion.TrySetResult(state))
        {
            _unsubscribe.Dispose();
            _timeout.Dispose();
        }

        return true;
    }
}
