namespace JasperFx.Core
{
    public static class StreamExtensions
    {
        /// <summary>
        /// Read the contents of a Stream from its current location
        /// into a String
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static string ReadAllText(this Stream stream)
        {
            using var sr = new StreamReader(stream, leaveOpen: true);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// Read all the bytes in a Stream from its current
        /// location to a byte[] array
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static byte[] ReadAllBytes(this Stream stream)
        {
            using var content = new MemoryStream();
            stream.CopyTo(content);
            return content.ToArray();
        }

        /// <summary>
        /// Asynchronously read the contents of a Stream from its current location
        /// into a String
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static Task<string> ReadAllTextAsync(this Stream stream)
        {
            using var sr = new StreamReader(stream, leaveOpen: true);
            return sr.ReadToEndAsync();
        }

        /// <summary>
        /// Asynchronously read all the bytes in a Stream from its current
        /// location to a byte[] array
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static async Task<byte[]> ReadAllBytesAsync(this Stream stream)
        {
            using var content = new MemoryStream();
            await stream.CopyToAsync(content).ConfigureAwait(false);
            return content.ToArray();
        }
        
        /// <summary>
        /// Read Byte array from stream
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static async Task<byte[]> ReadBytesAsync(this Stream stream, long length)
        {
            var buffer = new byte[length];
            var totalRead = 0;
            int current;
            do
            {
                current = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead).ConfigureAwait(false);
                totalRead += current;
            } while (totalRead < length && current > 0);

            return buffer;
        }

        /// <summary>
        /// Tests a stream to see if it starts with an expected byte sequence
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        public static async Task<bool> ReadExpectedBufferAsync(this Stream stream, byte[] expected)
        {
            try
            {
                var bytes = await stream.ReadBytesAsync(expected.Length).ConfigureAwait(false);
                return expected.SequenceEqual(bytes);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Write a byte array to a stream
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public static Task SendBufferAsync(this Stream stream, byte[] buffer)
        {
            return stream.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}