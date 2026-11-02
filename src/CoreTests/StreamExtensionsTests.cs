using System.Text;
using JasperFx.Core;
using Shouldly;

namespace CoreTests;

public class StreamExtensionsTests
{
    [Fact]
    public void can_read_all_text_synchronously()
    {
        var helloWorld = "Hello world.";

        var stream = new MemoryStream();
        var writer = new StreamWriter(stream, Encoding.Unicode);
        writer.WriteLine(helloWorld);
        writer.Flush();

        stream.Position = 0;
        stream.ReadAllText().Trim()
            .ShouldBe(helloWorld);
    }

    [Fact]
    public async Task can_read_all_text_asynchronously()
    {
        var stream = new MemoryStream();
        new StreamWriter(stream) { AutoFlush = true }
            .WriteLine("Hello world.");

        stream.Position = 0;

        (await stream.ReadAllTextAsync()).Trim()
            .ShouldBe("Hello world.");
    }

    [Fact]
    public void read_all_bytes()
    {
        var helloWorld = "Hello world.";
        var bytes = Encoding.Unicode.GetBytes(helloWorld);

        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);

        stream.Position = 0;

        var actual = stream.ReadAllBytes();

        actual.ShouldBe(bytes);
    }

    [Fact]
    public async Task read_all_bytes_async()
    {
        var helloWorld = "Hello world.";
        var bytes = Encoding.Unicode.GetBytes(helloWorld);

        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);

        stream.Position = 0;

        var actual = await stream.ReadAllBytesAsync();

        actual.ShouldBe(bytes);
    }

    [Fact]
    public async Task read_expected_buffer_matches_the_exact_bytes()
    {
        var stream = new MemoryStream([1, 2, 3]);

        (await stream.ReadExpectedBufferAsync([1, 2, 3])).ShouldBeTrue();
    }

    [Fact]
    public async Task read_expected_buffer_rejects_different_bytes()
    {
        var stream = new MemoryStream([1, 2, 4]);

        (await stream.ReadExpectedBufferAsync([1, 2, 3])).ShouldBeFalse();
    }

    [Fact]
    public async Task read_expected_buffer_only_consumes_what_it_expects()
    {
        var stream = new MemoryStream([1, 2, 3]);

        (await stream.ReadExpectedBufferAsync([1, 2])).ShouldBeTrue();

        stream.Position.ShouldBe(2);
    }

    [Fact]
    public async Task read_expected_buffer_rejects_a_stream_that_ends_short_of_trailing_zeros()
    {
        var stream = new MemoryStream([1]);

        (await stream.ReadExpectedBufferAsync([1, 0])).ShouldBeFalse();
    }

    [Fact]
    public async Task read_expected_buffer_rejects_an_empty_stream_when_expecting_zeros()
    {
        var stream = new MemoryStream();

        (await stream.ReadExpectedBufferAsync([0, 0])).ShouldBeFalse();
    }

    [Fact]
    public async Task read_expected_buffer_keeps_reading_across_partial_reads()
    {
        var stream = new OneByteAtATimeStream([1, 2, 3]);

        (await stream.ReadExpectedBufferAsync([1, 2, 3])).ShouldBeTrue();
    }

    [Fact]
    public async Task read_bytes_keeps_reading_across_partial_reads()
    {
        var stream = new OneByteAtATimeStream([1, 2, 3]);

        (await stream.ReadBytesAsync(3)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    // Pinned, not endorsed. Wolverine's TCP listener reads its length prefix with this and treats all zeros
    // as nothing to do, which is how it ignores a connection that closes without sending anything, like a
    // TCP health probe. Throwing here would turn each of those into an error
    [Fact]
    public async Task read_bytes_leaves_the_rest_of_the_buffer_zeroed_on_a_short_read()
    {
        var stream = new MemoryStream([1]);

        (await stream.ReadBytesAsync(3)).ShouldBe(new byte[] { 1, 0, 0 });
    }

    [Fact]
    public async Task read_bytes_returns_zeros_from_an_empty_stream()
    {
        var stream = new MemoryStream();

        (await stream.ReadBytesAsync(4)).ShouldBe(new byte[4]);
    }

    private class OneByteAtATimeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}