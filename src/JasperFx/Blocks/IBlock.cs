namespace JasperFx.Blocks;

/// <summary>
/// JasperFx's core "block" abstraction around Channels
/// </summary>
public interface IBlock : IAsyncDisposable
{
    Task WaitForCompletionAsync();

    void Complete();
}

/// <summary>
/// Composable "block" of work against a stream of items
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IBlock<T> : IBlock
{
    /// <summary>
    /// Add a new item to this item to be processed in the background
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    ValueTask PostAsync(T item);
    
    /// <summary>
    /// Synchronous version to post an item to this block for background processing
    /// </summary>
    /// <param name="item"></param>
    void Post(T item);
    
    /// <summary>
    /// Chain a strictly sequential upstream block around this block for producer/consumer mechanics. Equivalent to chaining a
    /// TPL DataFlow TransformBlock ahead of the current block
    /// </summary>
    /// <param name="transformation"></param>
    /// <typeparam name="TBefore"></typeparam>
    /// <returns></returns>
    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, CancellationToken, Task<T>> transformation);
    
    /// <summary>
    /// Chain an upstream block around this block for producer/consumer mechanics with a maximum parallel action count. Equivalent to chaining a
    /// TPL DataFlow TransformBlock ahead of the current block
    /// </summary>
    /// <param name="parallelCount">The maximum number of parallel items that can be processed in the upstream transformation block</param>
    /// <param name="transformation"></param>
    /// <typeparam name="TBefore"></typeparam>
    /// <returns></returns>
    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, CancellationToken, Task<T>> transformation);
    
    /// <summary>
    /// Chain a strictly sequential upstream block around this block for producer/consumer mechanics. Equivalent to chaining a
    /// TPL DataFlow TransformBlock ahead of the current block
    /// </summary>
    /// <param name="transformation"></param>
    /// <typeparam name="TBefore"></typeparam>
    /// <returns></returns>
    public IBlock<TBefore> PushUpstream<TBefore>(Func<TBefore, T> transformation);
    
    /// <summary>
    /// Chain an upstream block around this block for producer/consumer mechanics with a maximum parallel action count. Equivalent to chaining a
    /// TPL DataFlow TransformBlock ahead of the current block
    /// </summary>
    /// <param name="parallelCount">The maximum number of parallel items that can be processed in the upstream transformation block</param>
    /// <param name="transformation"></param>
    /// <typeparam name="TBefore"></typeparam>
    /// <returns></returns>
    public IBlock<TBefore> PushUpstream<TBefore>(int parallelCount, Func<TBefore, T> transformation);
}

public static class BlockExtensions
{
    /// <summary>
    /// Chain an upstream buffering block around this block for batching mechanics
    /// </summary>
    /// <param name="block"></param>
    /// <param name="timeOut">The batching block will emit a current batch after this time even if the batch is not "full"</param>
    /// <param name="batchSize">The size of the batches emitted</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IBlock<T> BatchUpstream<T>(this IBlock<T[]> block, TimeSpan timeOut, int batchSize = 100)
    {
        return new BatchingChannel<T>(timeOut, block, batchSize);
    }
}