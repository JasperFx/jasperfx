namespace JasperFx.Blocks;

/// <summary>
/// Abstract a way we can retry on <typeparamref name="T"/> message
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IRetryBlock<T>
{
    /// <summary>
    /// Send <typeparamref name="T"/> message in async manner
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    Task PostAsync(T message);
}
