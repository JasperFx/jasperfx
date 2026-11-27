using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// One term of a derived consistency-boundary query: the events carrying <see cref="Tag"/>, whose value
/// the command supplies from <see cref="Member"/> (jasperfx#1297).
/// </summary>
/// <param name="Tag">The tag the term selects by.</param>
/// <param name="Member">The command member supplying the tag value, when a read named one.</param>
/// <param name="EventTypes">
///     The event types the term selects, in declaration order: every emitted event of the slice that carries
///     the tag — the slice's own append condition. The events a read model <em>folds</em> live on the model
///     (<see cref="EventModelDescriptor.BoundaryQueryFor"/> adds them), because a slice alone cannot see them.
/// </param>
public sealed record BoundaryQueryTerm(string Tag, string? Member, IReadOnlyList<TypeDescriptor> EventTypes)
{
    /// <summary>The event types the term selects. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<TypeDescriptor> EventTypes { get; init; } = EventTypes ?? Array.Empty<TypeDescriptor>();

    /// <summary>Value equality, comparing <see cref="EventTypes"/> element-wise (the jasperfx#853 lesson).</summary>
    public bool Equals(BoundaryQueryTerm? other)
        => other is not null && Tag == other.Tag && Member == other.Member && EventTypes.SequenceEqual(other.EventTypes);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tag);
        hash.Add(Member);
        foreach (var type in EventTypes) hash.Add(type);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The consistency boundary a command slice's reads imply (jasperfx#1297): one term per tag read, in the
/// dcb.events shape of a query — items of (event types, tag). Computed from
/// <see cref="EventModelSliceDescriptor.Reads"/> and <see cref="EventModelSliceDescriptor.EventTags"/> on every
/// read, like <see cref="EventModelSliceDescriptor.Elements"/>, so a deserializer ignores it and recomputes.
/// </summary>
/// <param name="Terms">The terms, one per distinct tag the slice reads by, in declaration order.</param>
public sealed record BoundaryQueryDescriptor(IReadOnlyList<BoundaryQueryTerm> Terms)
{
    /// <summary>The terms. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<BoundaryQueryTerm> Terms { get; init; } = Terms ?? Array.Empty<BoundaryQueryTerm>();

    /// <summary>No reads, so no boundary: the slice decides against single-stream aggregates or nothing.</summary>
    public bool IsEmpty => Terms.Count == 0;

    /// <summary>The empty query.</summary>
    public static BoundaryQueryDescriptor Empty { get; } = new(Array.Empty<BoundaryQueryTerm>());

    /// <summary>Value equality, comparing <see cref="Terms"/> element-wise (the jasperfx#853 lesson).</summary>
    public bool Equals(BoundaryQueryDescriptor? other)
        => other is not null && Terms.SequenceEqual(other.Terms);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var term in Terms) hash.Add(term);
        return hash.ToHashCode();
    }
}
