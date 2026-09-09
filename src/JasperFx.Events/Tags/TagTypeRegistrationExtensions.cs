namespace JasperFx.Events.Tags;

/// <summary>
/// Resolution of a tag <em>name</em> — the string a caller supplies in the lossy
/// <see cref="EventQuery.TagValues"/> / <c>QueryByTagsAsync(IReadOnlyDictionary&lt;string,string&gt;)</c>
/// form — against a store's registered tag graph. See jasperfx#801.
/// </summary>
/// <remarks>
/// Lives here rather than in each store because the name matching <em>is</em> the contract: a caller
/// holding a store descriptor and a name/value pair has no way to discover which spelling a given
/// engine accepts, so a store that matched only the CLR name while its sibling matched the table
/// suffix would make the same query answer differently per store — and the failure would be an
/// <see cref="ArgumentException"/> at runtime, per store, which is exactly what the shared abstraction
/// exists to prevent.
/// </remarks>
public static class TagTypeRegistrationExtensions
{
    /// <summary>
    /// The registration a tag name refers to, or <see langword="null"/> when the name matches none.
    /// A name matches case-insensitively against either the tag type's CLR simple name
    /// (<c>"StudentId"</c>) or its registered table suffix (<c>"student"</c>); the CLR name is tried
    /// across every registration before the suffix, so a suffix that collides with another tag type's
    /// name cannot shadow that type.
    /// </summary>
    /// <param name="registrations">The store's registered tag types.</param>
    /// <param name="tagName">The tag name to resolve.</param>
    public static ITagTypeRegistration? FindByTagName(
        this IEnumerable<ITagTypeRegistration> registrations, string tagName)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(tagName);

        // Materialized because both passes walk it, and the caller's source is typically a lazy
        // Select over the event graph.
        var all = registrations as IReadOnlyList<ITagTypeRegistration> ?? registrations.ToList();

        foreach (var registration in all)
        {
            if (string.Equals(registration.TagType.Name, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return registration;
            }
        }

        foreach (var registration in all)
        {
            if (string.Equals(registration.TableSuffix, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return registration;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="FindByTagName"/>, refusing an unknown name with an <see cref="ArgumentException"/>
    /// that lists what <em>is</em> registered. An unknown tag name is an error rather than an empty
    /// result on purpose: "that tag type does not exist here" and "no event carries that tag" must
    /// not read alike to a caller filtering events.
    /// </summary>
    /// <param name="registrations">The store's registered tag types.</param>
    /// <param name="tagName">The tag name to resolve.</param>
    /// <param name="parameterName">Parameter name to attribute the exception to.</param>
    /// <exception cref="ArgumentException">No registered tag type answers to that name.</exception>
    public static ITagTypeRegistration RequireByTagName(
        this IEnumerable<ITagTypeRegistration> registrations, string tagName, string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(tagName);

        var all = registrations as IReadOnlyList<ITagTypeRegistration> ?? registrations.ToList();

        return all.FindByTagName(tagName)
               ?? throw new ArgumentException(
                   $"Tag type '{tagName}' is not registered on this event store. Registered tag types: "
                   + string.Join(", ", all.Select(x => $"{x.TagType.Name} (\"{x.TableSuffix}\")")),
                   parameterName);
    }
}
