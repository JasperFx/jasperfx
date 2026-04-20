namespace JasperFx.Descriptors;

/// <summary>
/// Opt-in interface that lets a complex type render as an <see cref="OptionsValue"/>
/// with <see cref="PropertyType.StringArray"/>. The returned strings are joined into
/// the value's display and retained as the raw value for richer UI rendering.
/// Useful for types that are essentially a collection of human-readable items such
/// as rule sets, policies, or filters.
/// </summary>
public interface IOptionsValueAsStringArray
{
    /// <summary>
    /// Render the underlying collection as individual string entries.
    /// </summary>
    IReadOnlyList<string> ToOptionsValueStrings();
}
