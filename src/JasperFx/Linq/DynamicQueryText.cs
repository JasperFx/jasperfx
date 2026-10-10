namespace JasperFx.Linq;

/// <summary>
/// A predicate and an ordering as text, plus the typed values for the predicate's <c>@0</c>, <c>@1</c>, …
/// placeholders — the input to <see cref="DynamicQuery" /> (jasperfx#869).
/// </summary>
/// <param name="Where">
/// The predicate in the Dynamic LINQ dialect, e.g. <c>Status = "Open" and Total &gt; @0</c>. Null or blank
/// applies no filter.
/// </param>
/// <param name="OrderBy">
/// The ordering, e.g. <c>PlacedAt desc, Id</c>. Null or blank leaves the source's own order.
/// </param>
/// <param name="Arguments">
/// Values for the <c>@n</c> placeholders in <paramref name="Where" />. Prefer these to literals: a date
/// literal is a parse of text, and a typed value has no time zone or number-format question to answer.
/// </param>
public sealed record DynamicQueryText(string? Where, string? OrderBy = null, IReadOnlyList<object?>? Arguments = null)
{
    /// <summary>Neither a predicate nor an ordering.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Where) && string.IsNullOrWhiteSpace(OrderBy);
}

/// <summary>Which half of a <see cref="DynamicQueryText" /> a problem was found in.</summary>
public enum DynamicQueryClause
{
    /// <summary>The predicate.</summary>
    Where,

    /// <summary>The ordering.</summary>
    OrderBy
}
