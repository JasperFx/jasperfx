using System.Buffers;

namespace JasperFx.Events;

/// <summary>
/// Pluggable binary serializer for event data — lets individual event types opt out of the store's
/// JSON format in favor of a binary wire format (MessagePack, MemoryPack, compressed JSON, …).
/// </summary>
/// <remarks>
/// <para>
/// Originated in Marten (marten#4515) and promoted here so one serializer implementation serves
/// every Critter Stack store. A consumer that compiles the same source against more than one store —
/// the shape the document contracts already exist for — would otherwise need a separate,
/// per-store-namespaced copy of an identical two-method class per flavour, which is the concrete
/// cost this move removes.
/// </para>
/// <para>
/// The contract is deliberately only these two operations: serialize and deserialize. Everything else about the feature is a
/// storage concern that differs per store and stays there: which column carries the bytes, how a
/// row records that it is binary rather than JSON, and how the serializer for a given event type is
/// resolved at registration time.
/// </para>
/// <para>
/// One property of the storage design is worth stating here because it is what makes the feature
/// adoptable at all, and an implementing store should preserve it: JSON rows and binary rows coexist
/// in the same event table on a per-event-type basis, keyed off whether the binary column is null.
/// Turning a single event type binary is therefore an in-place change with no migration of existing
/// event data — previously written JSON rows keep reading through the JSON path forever.
/// </para>
/// <para>
/// ⚠️ A serializer is part of a store's read path for as long as any row it wrote still exists.
/// Removing a registration does not make the old rows readable again — it makes them unreadable —
/// so a serializer stays registered after the event type stops being written binary.
/// </para>
/// </remarks>
public interface IEventBinarySerializer
{
    /// <summary>
    /// Serialize an event data instance to bytes.
    /// </summary>
    /// <param name="type">The runtime CLR type of the event data.</param>
    /// <param name="data">The event data to serialize.</param>
    byte[] Serialize(Type type, object data);

    /// <summary>
    /// Deserialize bytes back into an event data instance.
    /// </summary>
    /// <param name="type">The target CLR type to deserialize into.</param>
    /// <param name="data">The bytes previously produced by <see cref="Serialize" />.</param>
    object Deserialize(Type type, byte[] data);

    /// <summary>
    /// Serialize an event data instance into a caller-supplied buffer, so a store can write into a
    /// pooled buffer instead of taking a fresh exact-length array for every event (jasperfx#1187).
    /// </summary>
    /// <remarks>
    /// The default copies the result of <see cref="Serialize(Type, object)" /> into
    /// <paramref name="destination" />, so a serializer that implements only the <c>byte[]</c> pair
    /// keeps working through this member. Override it when the underlying library can write to an
    /// <see cref="IBufferWriter{T}" /> directly (System.Text.Json, MessagePack and MemoryPack all can);
    /// the bytes written must be exactly what <see cref="Serialize(Type, object)" /> would return, because
    /// a store may use either member to write and either <c>Deserialize</c> overload to read.
    /// </remarks>
    /// <param name="type">The runtime CLR type of the event data.</param>
    /// <param name="data">The event data to serialize.</param>
    /// <param name="destination">The buffer to append the serialized bytes to.</param>
    void Serialize(Type type, object data, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Serialize(type, data));
    }

    /// <summary>
    /// Deserialize an event data instance from a span, so a store can read from a slice of a rented
    /// buffer instead of first copying the column into a new exact-length array (jasperfx#1187).
    /// </summary>
    /// <remarks>
    /// The default copies <paramref name="data" /> to an array and calls
    /// <see cref="Deserialize(Type, byte[])" />, so a serializer that implements only the <c>byte[]</c>
    /// pair keeps working through this member. Override it when the underlying library can read a span
    /// directly. Deserialization is synchronous, so a span is enough: the caller owns the memory behind
    /// it only for the duration of the call, and an implementation must not hold on to it.
    /// </remarks>
    /// <param name="type">The target CLR type to deserialize into.</param>
    /// <param name="data">The bytes previously produced by either <c>Serialize</c> overload.</param>
    object Deserialize(Type type, ReadOnlySpan<byte> data) => Deserialize(type, data.ToArray());
}
