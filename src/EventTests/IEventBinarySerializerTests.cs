using System.Buffers;
using System.Text.Json;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// The span and <see cref="IBufferWriter{T}" /> members added in a minor (jasperfx#1187) are default
/// interface members over the original <c>byte[]</c> pair, so a serializer written against 3.2 has to keep
/// working when a store switches to the new members.
/// </summary>
public class IEventBinarySerializerTests
{
    private readonly IEventBinarySerializer theArrayOnlySerializer = new ArrayOnlyJsonSerializer();

    [Fact]
    public void a_serializer_with_only_the_array_pair_round_trips_through_the_new_members()
    {
        var writer = new ArrayBufferWriter<byte>();
        theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl"), writer);

        theArrayOnlySerializer.Deserialize(typeof(NameAdded), writer.WrittenSpan)
            .ShouldBe(new NameAdded("Carl"));
    }

    [Fact]
    public void the_buffer_writer_member_writes_the_same_bytes_as_the_array_member()
    {
        var writer = new ArrayBufferWriter<byte>();
        theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl"), writer);

        writer.WrittenSpan.ToArray()
            .ShouldBe(theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl")));
    }

    [Fact]
    public void the_buffer_writer_member_appends_to_what_is_already_in_the_buffer()
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write("prefix"u8);

        theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl"), writer);

        var written = writer.WrittenSpan;
        written[..6].ToArray().ShouldBe("prefix"u8.ToArray());
        theArrayOnlySerializer.Deserialize(typeof(NameAdded), written[6..]).ShouldBe(new NameAdded("Carl"));
    }

    [Fact]
    public void the_span_member_reads_a_slice_of_a_larger_rented_buffer()
    {
        var bytes = theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl"));

        // A rented array is usually longer than the payload; only the slice may be read
        var rented = ArrayPool<byte>.Shared.Rent(bytes.Length + 64);
        try
        {
            rented.AsSpan().Fill(0xFF);
            bytes.CopyTo(rented, 10);

            theArrayOnlySerializer.Deserialize(typeof(NameAdded), rented.AsSpan(10, bytes.Length))
                .ShouldBe(new NameAdded("Carl"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Fact]
    public void a_null_destination_is_refused()
    {
        Should.Throw<ArgumentNullException>(() =>
            theArrayOnlySerializer.Serialize(typeof(NameAdded), new NameAdded("Carl"), null!));
    }

    [Fact]
    public void a_serializer_that_overrides_the_new_members_is_called_through_the_interface()
    {
        var spanSerializer = new SpanAwareJsonSerializer();
        IEventBinarySerializer serializer = spanSerializer;

        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(typeof(NameAdded), new NameAdded("Carl"), writer);
        serializer.Deserialize(typeof(NameAdded), writer.WrittenSpan).ShouldBe(new NameAdded("Carl"));

        spanSerializer.BufferWriterCalls.ShouldBe(1);
        spanSerializer.SpanCalls.ShouldBe(1);
        spanSerializer.ArrayCalls.ShouldBe(0);
    }

    [Fact]
    public void an_array_argument_still_binds_to_the_array_overload()
    {
        var spanSerializer = new SpanAwareJsonSerializer();
        IEventBinarySerializer serializer = spanSerializer;

        var bytes = serializer.Serialize(typeof(NameAdded), new NameAdded("Carl"));
        serializer.Deserialize(typeof(NameAdded), bytes).ShouldBe(new NameAdded("Carl"));

        spanSerializer.ArrayCalls.ShouldBe(2);
        spanSerializer.SpanCalls.ShouldBe(0);
    }

    public record NameAdded(string Name);

    /// <summary>Written the way every serializer was before 3.3: only the two abstract members.</summary>
    public class ArrayOnlyJsonSerializer : IEventBinarySerializer
    {
        public byte[] Serialize(Type type, object data) => JsonSerializer.SerializeToUtf8Bytes(data, type);

        public object Deserialize(Type type, byte[] data) => JsonSerializer.Deserialize(data, type)!;
    }

    public class SpanAwareJsonSerializer : IEventBinarySerializer
    {
        public int ArrayCalls { get; private set; }
        public int BufferWriterCalls { get; private set; }
        public int SpanCalls { get; private set; }

        public byte[] Serialize(Type type, object data)
        {
            ArrayCalls++;
            return JsonSerializer.SerializeToUtf8Bytes(data, type);
        }

        public object Deserialize(Type type, byte[] data)
        {
            ArrayCalls++;
            return JsonSerializer.Deserialize(data, type)!;
        }

        public void Serialize(Type type, object data, IBufferWriter<byte> destination)
        {
            BufferWriterCalls++;
            using var writer = new Utf8JsonWriter(destination);
            JsonSerializer.Serialize(writer, data, type);
        }

        public object Deserialize(Type type, ReadOnlySpan<byte> data)
        {
            SpanCalls++;
            return JsonSerializer.Deserialize(data, type)!;
        }
    }
}
