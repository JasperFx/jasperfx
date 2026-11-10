using System.Globalization;

namespace JasperFx.Events.Tags;

/// <summary>
///     Converts a tag value (the primitive returned by
///     <see cref="ITagTypeRegistration.ExtractValue"/>) to its canonical string form, for anywhere a
///     tag value has to be text rather than its native type: the heterogeneous tag-version side tables
///     keyed across every registered tag type, and a stream key derived from a tag.
/// </summary>
/// <remarks>
///     <para>
///     Stable formatting matters: the same tag value has to produce the same string across processes,
///     cultures and .NET runtimes, or two concurrent appenders lock different rows and neither sees the
///     other.
///     </para>
///     <list type="bullet">
///       <item><description><see cref="Guid"/>: "d" (lowercase, hyphenated) format.</description></item>
///       <item><description><see cref="string"/>: passed through.</description></item>
///       <item><description>Anything <see cref="IFormattable"/>: <see cref="CultureInfo.InvariantCulture"/>.</description></item>
///     </list>
///     <para>
///     The encoding is persisted by Marten and Polecat, in <c>mt_dcb_tag_version</c> and
///     <c>pc_dcb_tag_version</c>, so changing it orphans every row already written. Lifted unchanged from
///     their identical copies (jasperfx#1044).
///     </para>
/// </remarks>
public static class TagValueStringifier
{
    /// <summary>
    ///     The canonical string form of a tag value.
    /// </summary>
    /// <param name="value">The unwrapped primitive, not the strong-typed tag.</param>
    public static string Stringify(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string s => s,
            Guid g => g.ToString("d"),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
