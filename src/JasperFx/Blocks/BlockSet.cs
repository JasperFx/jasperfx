namespace JasperFx.Blocks;

/// <summary>
/// Helps to chain channels for producer/consumer usages
/// </summary>
/// <typeparam name="T"></typeparam>
public class BlockSet<T> : IBlock<T>
{
    private readonly List<IBlock> _blocks;
    private readonly IBlock<T> _top;

    public BlockSet(IBlock<T> top, List<IBlock> previous)
    {
        // Copy rather than mutate: the caller may be an existing BlockSet handing over its own
        // _blocks list, and inserting into that shared list would corrupt the original set.
        _blocks = new List<IBlock>(previous.Count + 1) { top };
        _blocks.AddRange(previous);
        _top = top;
    }

    /// <summary>
    /// The total number of items buffered or in flight across the WHOLE chain — the top stage plus
    /// every downstream block. Consumers (e.g. Wolverine's back-pressure agent reading a listener's
    /// QueueCount) treat this as "work not yet finished", so an item that has moved from the top
    /// stage into a downstream block must still be counted.
    /// </summary>
    public uint Count
    {
        get
        {
            return (uint)_blocks.Sum(x => (long)x.Count);
        }
    }

    /// <summary>
    /// Delegates to the top block of the set -- the one whose processing action runs first for
    /// items posted to this set
    /// </summary>
    public Action<T, Exception> OnError
    {
        get => _top.OnError;
        set => _top.OnError = value;
    }

    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new Block<TBefore>(async (item, token) =>
        {
            var transformed = await transformation(item, token);
            if (transformed != null)
            {
                await _top.PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new Block<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = await transformation(item, token);
            if (transformed != null)
            {
                await _top.PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation)
    {
        var top = new Block<TBefore>(async (item, token) =>
        {
            var transformed = transformation(item);
            if (transformed != null)
            {
                await _top.PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, _blocks);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation)
    {
        var top = new Block<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = transformation(item);
            if (transformed != null)
            {
                await _top.PostAsync(transformed);  
            }
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