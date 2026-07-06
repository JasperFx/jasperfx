using System.Diagnostics;
using System.Threading.Channels;
using JasperFx.Core;

namespace JasperFx.Blocks;

public class Block<T> : BlockBase<T>
{
    /// <summary>
    /// The default bounded capacity for a Block's underlying channel. Writers that outrun the readers
    /// are throttled (back pressure) rather than dropped once this many items are buffered.
    /// </summary>
    public const int DefaultBoundedCapacity = 10000;

    /// <summary>
    /// Pass as the boundedCapacity to build a Block backed by an unbounded channel. Use this for blocks
    /// that may legitimately re-enqueue onto themselves from within their own processing action (which
    /// would deadlock against back pressure) or where callers must never block when posting.
    /// </summary>
    public const int Unbounded = -1;

    private readonly Func<T, CancellationToken, Task> _action;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task[] _tasks;
    private bool _latched;
    private uint _count = 0;

    public Block(Func<T, CancellationToken, Task> action) : this(1, action)
    {

    }

    public Block(Action<T> action) : this(1, (item, _) =>
    {
        action(item);
        return Task.CompletedTask;
    }){}

    /// <summary>
    /// A CancellationToken for the overarching process
    /// </summary>
    public CancellationToken Cancellation { get; set;  } = CancellationToken.None;

    public Block(int parallelCount, Func<T, CancellationToken, Task> action)
        : this(parallelCount, DefaultBoundedCapacity, action)
    {
    }

    /// <param name="parallelCount">The number of parallel readers processing the channel</param>
    /// <param name="boundedCapacity">
    /// The maximum number of buffered items before writers are throttled with back pressure. Pass
    /// <see cref="Unbounded"/> for an unbounded channel that never blocks or drops on write.
    /// </param>
    /// <param name="action">The processing action applied to each item</param>
    public Block(int parallelCount, int boundedCapacity, Func<T, CancellationToken, Task> action)
    {
        _action = action;

        // A bounded channel using BoundedChannelFullMode.Wait gives us back pressure: writers wait for
        // capacity rather than silently dropping items (GH-3287). Post() honors this by blocking-waiting
        // for room when TryWrite fails. Unbounded channels never block or drop, but grow with memory.
        _channel = boundedCapacity == Unbounded
            ? Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = parallelCount == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            })
            : Channel.CreateBounded<T>(new BoundedChannelOptions(boundedCapacity)
            {
                SingleReader = parallelCount == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait
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

    public override async Task WaitForCompletionAsync()
    {
        Complete();

        if (_count == 0) return;

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

            if (!_channel.Reader.TryRead(out var item))
            {
                continue;
            }

            try
            {
                await _action(item, Cancellation);
            }
            catch (Exception e)
            {
                try
                {
                    _onError(item, e);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine("Unable to apply error handling on channel");
                    Debug.WriteLine(exception);
                }
                Interlocked.Decrement(ref _count);
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

    public override void Post(T item)
    {
        if (_latched) return;

        Interlocked.Increment(ref _count);

        if (_channel.Writer.TryWrite(item))
        {
            return;
        }

        // The channel is bounded and currently full. Rather than silently dropping the item (the old
        // behavior that lost messages once >10k were queued, GH-3287), block-wait for capacity. This is
        // safe as long as Post() is not called from within this same Block's own processing action on a
        // saturated channel -- blocks that re-enqueue onto themselves must use the Unbounded capacity.
        try
        {
            _channel.Writer.WriteAsync(item, _cancellation.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Interlocked.Decrement(ref _count);
            try
            {
                _onError(item, e);
            }
            catch (Exception)
            {
                Debug.WriteLine($"Was not able to write {item} to the queue synchronously!");
            }
        }
    }

    public override ValueTask PostAsync(T item)
    {
        if (_latched) return ValueTask.CompletedTask;
        
        Interlocked.Increment(ref _count);
        return _channel.Writer.WriteAsync(item, _cancellation.Token);
    }

    public override uint Count => _count;

    public override ValueTask DisposeAsync()
    {
        _cancellation.SafeDispose();

        foreach (var task in _tasks)
        {
            task.SafeDispose();   
        }

        return new ValueTask();
    }

    public override void Complete()
    {
        if (_latched) return;
        
        _latched = true;

        _channel.Writer.TryComplete();
    }
    

}