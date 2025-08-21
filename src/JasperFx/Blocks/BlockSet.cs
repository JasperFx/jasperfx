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
        previous.Insert(0, top);
        _blocks = previous;
        _top = top;
    }

    public uint Count
    {
        get
        {
            return _top.Count + (uint)_blocks.Sum(x => x.Count);
        }
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