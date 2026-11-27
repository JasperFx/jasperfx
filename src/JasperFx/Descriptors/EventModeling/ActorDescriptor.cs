using System.Text.Json.Serialization;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Who triggers a slice — the actor lane of an Event Modeling board (jasperfx#1298). See
/// <see cref="EventModelSliceDescriptor.Actor"/>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="EventModelSliceDescriptor.TriggerLabel"/>, which is the sentence ("Agent clicks
/// Escalate"), and from <see cref="EventModelSliceDescriptor.TriggeredBy"/>, which only says a person is
/// involved. Declared with <c>Actor("Volunteer")</c> or by a role verb after <c>TriggeredBy(label, Human)</c>;
/// filled on import from actor lanes and <c>actors[] {name, rolesRequired}</c>; the HTTP source fills required
/// roles from authorization metadata where present. Declared and derived required roles may disagree, which is
/// a real finding.
/// </remarks>
/// <param name="Name">The actor's name: a role or persona such as <c>Volunteer</c> or <c>Coordinator</c>.</param>
/// <param name="RequiredRoles">Authorization roles the actor needs, when the slice requires any.</param>
// Two constructors, so System.Text.Json needs telling which one to use (the jasperfx#859 lesson).
[method: JsonConstructor]
public sealed record ActorDescriptor(string Name, IReadOnlyList<string> RequiredRoles)
{
    /// <summary>An actor with no required roles.</summary>
    public ActorDescriptor(string name) : this(name, Array.Empty<string>())
    {
    }

    /// <summary>Authorization roles the actor needs. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = RequiredRoles ?? Array.Empty<string>();

    /// <summary>Display form: the name, then the roles in brackets when there are any.</summary>
    public override string ToString()
        => RequiredRoles.Count == 0 ? Name : $"{Name} [{string.Join(", ", RequiredRoles)}]";

    // A record compares a collection member by reference (the jasperfx#853 lesson), so two actors naming the
    // same roles would be unequal and a wire round trip would never equal what went in.

    /// <summary>Value equality, comparing <see cref="RequiredRoles"/> element-wise.</summary>
    public bool Equals(ActorDescriptor? other)
        => other is not null
           && Name == other.Name
           && RequiredRoles.SequenceEqual(other.RequiredRoles, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        foreach (var role in RequiredRoles) hash.Add(role, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
