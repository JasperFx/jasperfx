namespace JasperFx.Core
{
    public static class DisposableExtensions
    {
        /// <summary>
        /// Attempts to call Dispose(), but swallows and discards any
        /// exceptions thrown
        /// </summary>
        /// <param name="disposable"></param>
        public static void SafeDispose(this IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // That's right, swallow that exception
            }
        }

        /// <summary>
        /// Will loop through the collection and call DisposeAsync() on
        /// any member that is IAsyncDisposable, and Dispose() on any other
        /// item that is IDisposable
        /// </summary>
        /// <param name="objects"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static async ValueTask MaybeDisposeAllAsync<T>(this IEnumerable<T> objects)
        {
            foreach (var o in objects)      
            {
                if (o is IAsyncDisposable ad)
                {
                    await ad.DisposeAsync();
                }
                else if (o is IDisposable d)
                {
                    d.Dispose();
                }
            }
        }

    }
}