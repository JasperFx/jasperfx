using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using JasperFx.Core.Reflection;

namespace JasperFx.Pagination;

/// <summary>
///     The keyset (seek) pagination cursor: an opaque, versioned base64-JSON array of the previous page's
///     last row's sort keys.
/// </summary>
/// <remarks>
///     <para>
///         jasperfx#1054. Lifted from Polecat's and Fisher's <c>CursorPagination</c>. The seek predicate
///         stays with each store, since it is SQL over the store's own members, and Marten keeps its own
///         long-typed paging.
///     </para>
///     <para>
///         <b>The bytes are the contract.</b> A cursor is handed to a client and comes back on a later
///         request, possibly to a newer deployment, so a change of spelling here invalidates every
///         cursor in flight. Each value is written with the call System.Text.Json's own converter for
///         its type makes, under the same default encoder, which is what the original
///         <c>JsonSerializer.Serialize(object?[])</c> wrote. <c>CursorCodecTests</c> pins exact strings
///         taken from each store's encoder.
///     </para>
///     <para>
///         Not <see cref="JsonSerializer" /> itself, because that overload is reflection based and Native
///         AOT disables it (polecat#743, fisher#412). Nothing here reaches it.
///     </para>
///     <para>
///         <b>The stores share a format and not a representation.</b> Polecat binds typed values and
///         Fisher binds what SQLite holds, so a decoded Guid is a <see cref="Guid" /> on one and lowercase
///         text on the other, and Fisher writes a <see cref="DateTimeOffset" /> in its fixed-width UTC
///         form where Polecat keeps the offset. That is why <see cref="NormalizeKey" /> and
///         <see cref="ConvertSlot" /> are virtual, and the reason a timestamp-keyed cursor is not portable
///         between the two. What this class fixes is everything around them. The defaults are Polecat's.
///     </para>
///     <para>
///         <b>Values are typed on decode by the query's ordering keys, never by the cursor.</b> The
///         payload carries no type information, so a hand-edited cursor can change values but not what
///         they are read as. A cursor is request input, so every way of malforming it arrives as an
///         <see cref="ArgumentException" /> an endpoint can answer with a 400.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: ValueTypeInfo.ForType is how a strong-typed key is carried as the value it wraps. A wrapper trimmed of its members fails ForType and takes the same refusal as any other unsupported key.")]
[UnconditionalSuppressMessage("Trimming", "IL2067:UnrecognizedReflectionPattern", Justification = "See above.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:UnrecognizedReflectionPattern", Justification = "See above.")]
public class CursorCodec
{
    /// <summary>
    ///     The prefix of every cursor this writes.
    /// </summary>
    public const string Version = "v1:";

    /// <summary>
    ///     The codec for a store that binds typed values.
    /// </summary>
    public static CursorCodec Default { get; } = new();

    /// <summary>
    ///     Refuse an ordering that cannot support a seek.
    /// </summary>
    /// <remarks>
    ///     The terminal key must be the identity, so the ordering is a <em>total</em> order. Without that,
    ///     rows tied on the sort key have no defined order between them and a seek boundary lands in the
    ///     middle of the tie, skipping some and repeating others, silently and only when there are ties.
    /// </remarks>
    /// <param name="orderingKeys">
    ///     The ordering's keys in order, in whatever the store resolves a member to. Null for a key that
    ///     is not a document member.
    /// </param>
    /// <param name="isIdentity">Whether a key is the document's identity.</param>
    public void ValidateOrdering<TKey>(IReadOnlyList<TKey?> orderingKeys, Func<TKey, bool> isIdentity)
        where TKey : class
    {
        if (orderingKeys.Count == 0)
        {
            throw OrderingRefused(
                "Keyset pagination requires an OrderBy. Add one whose terminal key is the document identity, "
                + "for example OrderBy(x => x.Landed).ThenBy(x => x.Id).");
        }

        if (orderingKeys.Any(x => x is null))
        {
            throw OrderingRefused(
                "Keyset pagination needs every ordering key to be a document member, so its value can be "
                + "carried in the cursor. An ordering over a projection or a group aggregate cannot.");
        }

        if (!isIdentity(orderingKeys[^1]!))
        {
            throw OrderingRefused(
                "Keyset pagination requires the terminal ordering key to be the document identity, so the "
                + "ordering is a total order. Otherwise rows tied on the sort key would be skipped or "
                + "repeated across pages. End the ordering with ThenBy(x => x.Id).");
        }
    }

    /// <summary>
    ///     Encode the sort keys of the last row on a page.
    /// </summary>
    /// <exception cref="NotSupportedException">A key is of a type no cursor can carry.</exception>
    public string Encode(IReadOnlyList<object?> keyValues)
    {
        ArgumentNullException.ThrowIfNull(keyValues);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var value in keyValues)
            {
                writeKey(writer, NormalizeKey(value));
            }

            writer.WriteEndArray();
        }

        return Version + Convert.ToBase64String(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Decode a cursor into one value per ordering key.
    /// </summary>
    /// <param name="cursor">The cursor, which is client input.</param>
    /// <param name="keyTypes">The CLR type of each ordering key, which is what types the values.</param>
    /// <exception cref="ArgumentException">
    ///     The cursor is not one of ours, is malformed, or does not bind to this query's ordering.
    /// </exception>
    /// <exception cref="BadLinqExpressionException">
    ///     The query orders by a key no cursor can carry. That is the query's fault rather than the
    ///     cursor's, so it is deliberately not an <see cref="ArgumentException" />.
    /// </exception>
    public object?[] Decode(string cursor, IReadOnlyList<Type> keyTypes)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(keyTypes);

        if (!cursor.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unrecognized or unversioned cursor. Expected a '{Version}' prefix.", nameof(cursor));
        }

        var slots = parseSlots(cursor);

        if (slots.Length != keyTypes.Count)
        {
            throw new ArgumentException(
                $"Cursor carries {slots.Length} key(s) but the query orders by {keyTypes.Count}. The cursor does "
                + "not match this query's ordering.", nameof(cursor));
        }

        var values = new object?[slots.Length];

        for (var i = 0; i < slots.Length; i++)
        {
            values[i] = bindSlot(slots[i], keyTypes[i], i);
        }

        return values;
    }

    /// <summary>
    ///     What to put in the cursor for a key read back from a row. Polecat's: a database null is a
    ///     null, and anything else is written as it is.
    /// </summary>
    protected virtual object? NormalizeKey(object? value) => value is DBNull ? null : value;

    /// <summary>
    ///     The value to bind for one slot, typed by the ordering key. A null slot never reaches this.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="keyType">
    ///     The type the key is stored as: nullable-stripped, and the inner type of a strong-typed key.
    /// </param>
    /// <remarks>
    ///     An <see cref="InvalidOperationException" />, <see cref="FormatException" /> or
    ///     <see cref="OverflowException" /> from here is reported as a cursor that does not bind, which is
    ///     what <see cref="JsonElement" /> throws for a slot of the wrong kind.
    /// </remarks>
    protected virtual object? ConvertSlot(JsonElement slot, Type keyType)
    {
        if (keyType == typeof(Guid)) return slot.GetGuid();
        if (keyType == typeof(string)) return slot.GetString();
        if (keyType == typeof(int)) return slot.GetInt32();
        if (keyType == typeof(long)) return slot.GetInt64();
        if (keyType == typeof(short)) return slot.GetInt16();
        if (keyType == typeof(byte)) return slot.GetByte();
        if (keyType == typeof(bool)) return slot.GetBoolean();
        if (keyType == typeof(decimal)) return slot.GetDecimal();
        if (keyType == typeof(double)) return slot.GetDouble();
        if (keyType == typeof(float)) return slot.GetSingle();
        if (keyType == typeof(DateTimeOffset)) return slot.GetDateTimeOffset();
        if (keyType == typeof(DateTime)) return slot.GetDateTime();

        throw new BadLinqExpressionException(
            $"Keyset (cursor) pagination does not support an ordering key of type '{keyType.Name}'. "
            + "Order by primitive/Guid/DateTime(Offset) keys with the document identity as the terminal key.");
    }

    /// <summary>
    ///     The exception for an ordering that cannot support a seek.
    /// </summary>
    /// <remarks>
    ///     <see cref="BadLinqExpressionException" />, which is what the lifted exception is for and what
    ///     Fisher throws. Polecat threw <see cref="InvalidOperationException" />, and neither derives from
    ///     the other, so a store with that behaviour pinned overrides this.
    /// </remarks>
    protected virtual Exception OrderingRefused(string message) => new BadLinqExpressionException(message);

    // Parsed from the bytes rather than from a decoded string, which is Polecat's way. Fisher's replaced
    // invalid UTF-8 with U+FFFD, so a tampered string came through as a different string instead of failing.
    private static JsonElement[] parseSlots(string cursor)
    {
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(cursor[Version.Length..]));

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray(),

                // A JSON null reads as no keys, as JsonSerializer read it.
                JsonValueKind.Null => [],
                _ => throw new JsonException("A cursor payload is a JSON array.")
            };
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new ArgumentException("Malformed cursor payload.", nameof(cursor), e);
        }
    }

    private object? bindSlot(JsonElement slot, Type keyType, int index)
    {
        if (slot.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // fisher#62, the marten#5029 class, and the case Polecat let through: the payload's shape is
        // checked by now, but binding a slot to its key's type is a second way a client-supplied cursor can
        // be wrong, and JsonElement reports that as an exception an endpoint would read as its own fault.
        object? value;
        try
        {
            value = ConvertSlot(slot, storedKeyType(keyType));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"This cursor's key {index} does not bind to '{keyType.Name}'. It was issued against a different "
                + "ordering, or has been tampered with.", "cursor", e);
        }

        // JsonElement reads 1e400 as infinity without complaint, and no store can bind that.
        if (value is double.PositiveInfinity or double.NegativeInfinity
            or float.PositiveInfinity or float.NegativeInfinity)
        {
            throw new ArgumentException(
                $"This cursor's key {index} does not bind to '{keyType.Name}'. It has been tampered with.", "cursor");
        }

        return value;
    }

    // A strong-typed key is carried as the value it wraps. Fisher does this and Polecat does not, which
    // leaves a Polecat document keyed by a wrapper unable to get past its first page: the terminal key's
    // member type is the wrapper, and ConvertSlot refuses it.
    private static Type storedKeyType(Type keyType)
    {
        var target = Nullable.GetUnderlyingType(keyType) ?? keyType;

        if (target.IsPrimitive || target.IsEnum || target.Namespace == "System")
        {
            return target;
        }

        return tryFindWrapper(target, out var wrapper) ? wrapper.SimpleType : target;
    }

    private static bool tryFindWrapper(Type type, [NotNullWhen(true)] out ValueTypeInfo? wrapper)
    {
        try
        {
            wrapper = ValueTypeInfo.ForType(type);
            return true;
        }
        catch (InvalidValueTypeException)
        {
            wrapper = null;
            return false;
        }
    }

    private void writeKey(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;

            // An enum is its number, as System.Text.Json's default EnumConverter writes it.
            case Enum enumValue
                when Type.GetTypeCode(Enum.GetUnderlyingType(enumValue.GetType())) == TypeCode.UInt64:
                writer.WriteNumberValue(Convert.ToUInt64(enumValue, CultureInfo.InvariantCulture));
                break;
            case Enum enumValue:
                writer.WriteNumberValue(Convert.ToInt64(enumValue, CultureInfo.InvariantCulture));
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case char character:
                writer.WriteStringValue(character.ToString());
                break;
            case DateTimeOffset timestamp:
                writer.WriteStringValue(timestamp);
                break;
            case DateTime timestamp:
                writer.WriteStringValue(timestamp);
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;

            // The TimeSpan constant format, which is what System.Text.Json's converters for both emit.
            case TimeOnly time:
                writer.WriteStringValue(time.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture));
                break;
            case TimeSpan span:
                writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture));
                break;
            default:
                if (tryFindWrapper(value.GetType(), out var wrapper))
                {
                    writeKey(writer, NormalizeKey(wrapper.ValueProperty.GetValue(value)));
                    break;
                }

                // Polecat wrote these as ToString() and refused them on the next request. Refused here
                // instead, by name, so a client is never handed a cursor nobody can decode.
                throw new NotSupportedException(
                    $"Keyset pagination cannot carry an ordering key of type '{value.GetType().Name}' in a cursor. "
                    + "Order by members holding a string, a number, a Guid, a timestamp, a date or a time, "
                    + "ending with the document identity.");
        }
    }
}
