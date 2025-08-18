namespace JasperFx.Blocks;

public abstract class BlockBase<T> : IBlock<T>
{
    public abstract ValueTask DisposeAsync();
    public abstract Task WaitForCompletionAsync();
    public abstract void Complete();
    public abstract ValueTask PostAsync(T item);
    public abstract void Post(T item);

    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new Block<TBefore>(async (item, token) =>
        {
            var transformed = await transformation(item, token);
            if (transformed != null)
            {
                await PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation)
    {
        var top = new Block<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = await transformation(item, token);
            if (transformed != null)
            {
                await PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation)
    {
        var top = new Block<TBefore>(async (item, token) =>
        {
            var transformed = transformation(item);
            if (transformed != null)
            {
                await PostAsync(transformed);
            }
        });

        return new BlockSet<TBefore>(top, []);
    }

    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation)
    {
        var top = new Block<TBefore>(parallelCount, async (item, token) =>
        {
            var transformed = transformation(item);
            if (transformed != null)
            {
                await PostAsync(transformed);   
            }
        });

        return new BlockSet<TBefore>(top, []);
    }
}