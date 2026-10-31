using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;

namespace JasperFx.Events.Projections;

/// <summary>
///     The store's own serializer, as a projection step-through needs it: how a projection's state
///     becomes the JSON the store would persist, and how that JSON becomes a state again.
/// </summary>
/// <remarks>
///     <para>
///         ⚠️ <b>This is deliberately not a <c>Func&lt;T, T&gt;</c> copy delegate</b> (jasperfx#1049). The
///         fold mutates an aggregate in place, so a recorded step that shares a reference with it ends up
///         showing the final state, which is what Polecat's replay did. A copy delegate makes that one
///         identity lambda away, and a no-op compiles, runs and looks right on a one-event timeline.
///     </para>
///     <para>
///         So a store does not supply a copy. It supplies the two halves of a round trip, both required,
///         and every recorded step is the JSON. A <see cref="JsonElement" /> cannot alias the aggregate,
///         and a typed step is read back from that JSON, so it cannot either: the deserializer is given
///         nothing that leads back to the live instance.
///     </para>
/// </remarks>
public sealed class ProjectionStateSerializer
{
    private readonly Func<object?, JsonElement?> _serialize;
    private readonly Func<JsonElement, Type, object?>? _deserialize;

    /// <param name="serialize">The state as the JSON the store would persist for it.</param>
    /// <param name="deserialize">A new instance of the given type, read from that JSON.</param>
    public ProjectionStateSerializer(Func<object, JsonElement> serialize,
        Func<JsonElement, Type, object?> deserialize)
    {
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);

        _serialize = state => state is null ? null : serialize(state);
        _deserialize = deserialize;
    }

    /// <summary>
    ///     The write-only form behind <see cref="Aggregation.ISteppableAggregation{TQuerySession}" />'s
    ///     original delegate overload, which never asked for a way back.
    /// </summary>
    internal ProjectionStateSerializer(Func<object?, JsonElement?> serialize)
    {
        _serialize = serialize;
    }

    internal bool CanDeserialize => _deserialize != null;

    internal JsonElement? Serialize(object? state)
    {
        if (state is null) return null;

        try
        {
            return _serialize(state);
        }
        catch (Exception e)
        {
            throw new ProjectionStateRoundTripException(state.GetType(), null, "could not be serialized", e);
        }
    }

    /// <summary>
    ///     A new instance from <paramref name="json" />, with no claim that it holds everything the JSON
    ///     does. Only the raw form's starting state, where the JSON may be partial and the fold's first
    ///     snapshot is taken from the result anyway.
    /// </summary>
    internal T Deserialize<T>(JsonElement json, EventRecord? at = null)
    {
        object? state;

        try
        {
            state = _deserialize!(json, typeof(T));
        }
        catch (Exception e)
        {
            throw new ProjectionStateRoundTripException(typeof(T), at, "could not be deserialized", e);
        }

        return state is T typed
            ? typed
            : throw new ProjectionStateRoundTripException(typeof(T), at,
                $"deserialized to {(state is null ? "null" : $"a '{state.GetType().FullNameInCode()}'")}");
    }

    /// <summary>
    ///     A new instance from <paramref name="json" /> that is proven to carry what the JSON carries.
    /// </summary>
    /// <remarks>
    ///     A serializer loses state without complaint: <c>public int Count { get; private set; }</c>
    ///     writes its value and reads back zero, and a getter-only list reads back empty. Every step of
    ///     such a timeline is then wrong and nothing says so, which is the failure this type exists to
    ///     prevent wearing a different hat. Serializing the copy again is what notices, and it costs one
    ///     more serialization on a path that is a person looking at a console.
    /// </remarks>
    internal T RoundTrip<T>(JsonElement json, EventRecord? at = null)
    {
        var copy = Deserialize<T>(json, at);
        var again = Serialize(copy);

        return again is { } written && JsonElement.DeepEquals(written, json)
            ? copy
            : throw new ProjectionStateRoundTripException(typeof(T), at,
                $"did not survive a trip through the store's serializer{describeLoss(json, again)}");
    }

    // Names only. These are property names off the user's aggregate, and the values are the user's data
    // on their way into an exception message and from there into a log.
    private static string describeLoss(JsonElement expected, JsonElement? actual)
    {
        if (expected.ValueKind != JsonValueKind.Object || actual is not { ValueKind: JsonValueKind.Object } read)
        {
            return string.Empty;
        }

        var lost = expected.EnumerateObject()
            .Where(x => !read.TryGetProperty(x.Name, out var value) || !JsonElement.DeepEquals(x.Value, value))
            .Select(x => x.Name)
            .ToArray();

        return lost.Length == 0 ? string.Empty : $": {string.Join(", ", lost)} came back different";
    }
}
