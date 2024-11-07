using System.Diagnostics;

namespace JasperFx.Events.Projections;

/// <summary>
///     Used mostly by tests to listen for expected shard events or progress
/// </summary>
internal class ShardStatusWatcher: IObserver<ShardState>
{
    private readonly TaskCompletionSource<ShardState> _completion;
    private readonly Func<ShardState, bool> _condition;
    private readonly IDisposable _unsubscribe;

    public ShardStatusWatcher(ShardStateTracker tracker, ShardState expected, TimeSpan timeout)
    {
        _condition = x => x.ShardName == expected.ShardName && x.Sequence >= expected.Sequence;
        _completion = new TaskCompletionSource<ShardState>();


        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                $"Shard {expected.ShardName} did not reach sequence number {expected.Sequence} in the time allowed"));
        });

        _unsubscribe = tracker.Subscribe(this);
    }

    public ShardStatusWatcher(string description, Func<ShardState, bool> condition, ShardStateTracker tracker,
        TimeSpan timeout)
    {
        _condition = condition;
        _completion = new TaskCompletionSource<ShardState>();


        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                $"{description} was not detected in the time allowed"));
        });

        _unsubscribe = tracker.Subscribe(this);

        Debug.WriteLine("Subscribed to watch shard state: " + description);
    }

    public Task<ShardState> Task => _completion.Task;

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
        _completion.SetException(error);
    }

    public void OnNext(ShardState value)
    {
        if (_condition(value))
        {
            _completion.SetResult(value);
            _unsubscribe.Dispose();
        }
    }
}
