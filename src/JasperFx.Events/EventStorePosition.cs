using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace JasperFx.Events;

/// <summary>
///     A point in an event store that a caller can wait on: the highest event sequence a unit of work
///     appended, and where that sequence lives. Read it from a session after <c>SaveChangesAsync</c> and hand
///     it to <see cref="IEventStore.WaitForProjectionsAsync(EventStorePosition, IReadOnlyList{Type}, TimeSpan, CancellationToken)" />
///     to read your own writes from an asynchronous projection (marten#6004).
/// </summary>
/// <remarks>
///     <para>
///         The database is part of the position because a multi-database store has one sequence per database.
///         The tenant is part of it because a store that partitions its events by tenant has one sequence per
///         tenant. On a store that does neither, both may be null and only <see cref="Sequence" /> matters.
///     </para>
///     <para>
///         The position is small and travels: it serializes as JSON as it is, and <see cref="ToString" /> and
///         <see cref="Parse" /> give a compact text form for an HTTP or message header.
///     </para>
/// </remarks>
/// <param name="DatabaseIdentifier">
///     The <see cref="IEventDatabase.Identifier" /> of the database the events went to, or null for a store
///     with one database.
/// </param>
/// <param name="TenantId">The tenant the events were appended for, or null.</param>
/// <param name="Sequence">The highest event sequence the unit of work appended.</param>
public sealed record EventStorePosition(string? DatabaseIdentifier, string? TenantId, long Sequence)
{
    private const char Separator = '/';

    /// <summary>
    ///     The compact text form, <c>{database}/{tenant}/{sequence}</c>, with the database and tenant percent
    ///     encoded and left empty when null. Safe to put in a header and read back with <see cref="Parse" />.
    /// </summary>
    public override string ToString()
    {
        return string.Concat(encode(DatabaseIdentifier), Separator.ToString(), encode(TenantId),
            Separator.ToString(), Sequence.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Read the text form written by <see cref="ToString" />.
    /// </summary>
    /// <exception cref="FormatException">The text is not a position.</exception>
    public static EventStorePosition Parse(string text)
    {
        if (TryParse(text, out var position)) return position;

        throw new FormatException(
            $"'{text}' is not an event store position. The expected form is '{{database}}/{{tenant}}/{{sequence}}'.");
    }

    /// <summary>
    ///     Read the text form written by <see cref="ToString" />, returning false rather than throwing for
    ///     anything else.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out EventStorePosition? position)
    {
        position = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(Separator);
        if (parts.Length != 3) return false;

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }

        position = new EventStorePosition(decode(parts[0]), decode(parts[1]), sequence);
        return true;
    }

    private static string encode(string? value) => value == null ? string.Empty : Uri.EscapeDataString(value);

    private static string? decode(string value) => value.Length == 0 ? null : Uri.UnescapeDataString(value);
}
