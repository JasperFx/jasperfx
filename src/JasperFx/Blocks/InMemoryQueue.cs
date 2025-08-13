using System.Diagnostics;
using System.Threading.Channels;
using JasperFx.Core;

namespace JasperFx.Blocks;

public interface IBlockSet<T> : IBlock<T>
{
    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation);
    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation);
    
    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation);
    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation);
}

public class BlockSet<T> : IBlockSet<T>
{
    private readonly List<IBlock> _blocks;
    private readonly IBlock<T> _top;

    public BlockSet(IBlock<T> top, List<IBlock> previous)
    {
        previous.Insert(0, top);
        _blocks = previous;
        _top = top;
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new InMemoryQueue<TBefore>(async (item, token) =>
        {
            var transformed = await transformation(item, token);
            await _top.PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new InMemoryQueue<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = await transformation(item, token);
            await _top.PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation)
    {
        var top = new InMemoryQueue<TBefore>(async (item, token) =>
        {
            var transformed = transformation(item);
            await _top.PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation)
    {
        var top = new InMemoryQueue<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = transformation(item);
            await _top.PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public async Task WaitForCompletionAsync()
    {
        foreach (var block in _blocks)
        {
            block.Complete();
            await block.WaitForCompletionAsync();
        }
    }

    public void Complete()
    {
        _top.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var block in _blocks)
        {
            await block.DisposeAsync();
        }
    }

    public ValueTask PostAsync(T item)
    {
        return _top.PostAsync(item);
    }

    public void Post(T item)
    {
        _top.Post(item);
    }
}

public interface IBlock : IAsyncDisposable
{
    Task WaitForCompletionAsync();

    void Complete();
}

public interface IBlock<T> : IBlock
{
    ValueTask PostAsync(T item);
    
    void Post(T item);
}

public class InMemoryQueue<T> : IBlock<T>, IBlockSet<T>
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

    public InMemoryQueue(Action<T> action) : this(1, (item, _) =>
    {
        action(item);
        return Task.CompletedTask;
    }){}
    
    /// <summary>
    /// A CancellationToken for the overarching process
    /// </summary>
    public CancellationToken Cancellation { get; set;  } = CancellationToken.None;

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
        
        //await _cancellation.CancelAsync();

        await Task.WhenAll(_tasks);

        while (!Cancellation.IsCancellationRequested && _count > 0)
        {
            try
            {
                var isData = await _channel.Reader.WaitToReadAsync(_cancellation.Token);
                if (!isData) return;
            }
            catch (TaskCanceledException )
            {
                return;
            }
            
            if (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    await _action(item, Cancellation);
                }
                catch (Exception e)
                {
                    // TODO - try/catch/finally this
                    _onError(item, e);
                    Interlocked.Decrement(ref _count);
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
    
    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new InMemoryQueue<TBefore>(async (item, token) =>
        {
            var transformed = await transformation(item, token);
            await PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new InMemoryQueue<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = await transformation(item, token);
            await PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation)
    {
        var top = new InMemoryQueue<TBefore>(async (item, token) =>
        {
            var transformed = transformation(item);
            await PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlockSet<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation)
    {
        var top = new InMemoryQueue<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = transformation(item);
            await PostAsync(transformed);
        });

        return new BlockSet<TBefore>(top, []);
    }
}