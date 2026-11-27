using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The consistency tags one event a slice emits carries (jasperfx#1297). See
/// <see cref="EventModelSliceDescriptor.EventTags"/>. The sibling of <see cref="EmittedEventStream"/>: that
/// says which stream an event goes to, this says which tags it carries, and an event may do both.
/// </summary>
/// <param name="Event">The emitted event.</param>
/// <param name="Tags">The names of the tags the event carries, as the model's <see cref="EventModelDescriptor.Tags"/> name them.</param>
public sealed record EmittedEventTags(TypeDescriptor Event, IReadOnlyList<string> Tags)
{
    /// <summary>The names of the tags the event carries. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? Array.Empty<string>();

    // A record compares a collection member by reference (the jasperfx#853 lesson); a wire descriptor wants
    // value equality so a round trip equals what went in.

    /// <summary>Value equality, comparing <see cref="Tags"/> element-wise.</summary>
    public bool Equals(EmittedEventTags? other)
        => other is not null && Event == other.Event && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Event);
        foreach (var tag in Tags) hash.Add(tag, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
