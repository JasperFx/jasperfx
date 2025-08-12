using System.Diagnostics;
using System.Threading.Channels;
using JasperFx.Core;

namespace JasperFx.Blocks;

public interface IBlock<T> : IAsyncDisposable
{
    ValueTask PostAsync(T item);
    Task WaitForCompletionAsync();
}

public class InMemoryQueue<T> : IBlock<T>
{
    private readonly Func<T, CancellationToken, Task> _action;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task[] _tasks;
    private bool _latched;

    public InMemoryQueue(Func<T, CancellationToken, Task> action) : this(1, action)
    {

    }

    public InMemoryQueue(int parallelCount, Func<T, CancellationToken, Task> action)
    {
        _action = action;
        
        // TODO -- what about on dropping????????
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(10000)
        {
            SingleReader = parallelCount == 1,
            SingleWriter = false
        });

        _tasks = new Task[parallelCount];
        for (int i = 0; i < parallelCount; i++)
        {
            _tasks[i] = Task.Run(processAsync, _cancellation.Token);
        }
    }

    private Action<T, Exception> _onError = (item, ex) =>
    {
        Debug.WriteLine("Error processing item " + item);
        Debug.WriteLine(ex.ToString());
    };

    public Action<T, Exception> OnError
    {
        get => _onError;
        set => _onError = value ?? throw new ArgumentNullException(nameof(OnError));
    }

    public async Task WaitForCompletionAsync()
    {
        _latched = true;
        
        _channel.Writer.Complete();

        if (_channel.Reader.Count == 0) return;

        await Task.WhenAll(_tasks);

        while (!_cancellation.IsCancellationRequested && _channel.Reader.Count > 0)
        {
            var item = await _channel.Reader.ReadAsync(_cancellation.Token);

            try
            {
                await _action(item, _cancellation.Token);
            }
            catch (Exception e)
            {
                _onError(item, e);
            }
        }
        
        Debug.WriteLine("What?");
    }

    private async Task processAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            await _channel.Reader.WaitToReadAsync();

            if (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    await _action(item, _cancellation.Token);
                }
                catch (Exception e)
                {
                    _onError(item, e);
                }
            }

            if (_latched && _channel.Reader.Count == 0) return;
        }
    }

    public ValueTask PostAsync(T item)
    {
        if (_latched) throw new InvalidOperationException("This SequentialQueue is latched");
        
        return _channel.Writer.WriteAsync(item, _cancellation.Token);
    }
    
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.SafeDispose();

        foreach (var task in _tasks)
        {
            task.SafeDispose();   
        }
    }
}