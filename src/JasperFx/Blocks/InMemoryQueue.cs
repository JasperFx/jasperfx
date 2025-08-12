using System.Diagnostics;
using System.Threading.Channels;
using JasperFx.Core;

namespace JasperFx.Blocks;

public interface IBlock<T> : IAsyncDisposable
{
    ValueTask PostAsync(T item);
    Task WaitForCompletionAsync();
    void Post(T item);
}

public class InMemoryQueue<T> : IBlock<T>
{
    private readonly Func<T, CancellationToken, Task> _action;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task[] _tasks;
    private bool _latched;
    private uint _count = 0;

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
        Complete();

        if (_count == 0) return;

        await Task.WhenAll(_tasks);

        while (!_cancellation.IsCancellationRequested && _count > 0)
        {
            var isData = await _channel.Reader.WaitToReadAsync(_cancellation.Token);
            if (!isData) return;

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
        }
    }

    private async Task processAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            var isData = await _channel.Reader.WaitToReadAsync(_cancellation.Token);
            if (!isData) return;

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
                finally
                {
                    Interlocked.Decrement(ref _count);
                }
            }
            
            
            if (_latched) return;
        }
    }

    public void Post(T item)
    {
        Interlocked.Increment(ref _count);
        if (!_channel.Writer.TryWrite(item))
        {
            Debug.WriteLine($"Was not able to write {item} to the queue synchronously!");
        }
    }

    public ValueTask PostAsync(T item)
    {
        if (_latched) throw new InvalidOperationException("This SequentialQueue is latched");
        
        Interlocked.Increment(ref _count);
        return _channel.Writer.WriteAsync(item, _cancellation.Token);
    }

    public uint Count => _count;

    public ValueTask DisposeAsync()
    {
        _cancellation.SafeDispose();

        foreach (var task in _tasks)
        {
            task.SafeDispose();   
        }

        return new ValueTask();
    }

    public void Complete()
    {
        if (_latched) return;
        
        _latched = true;

        _channel.Writer.TryComplete();
    }
}