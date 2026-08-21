using System.Text.Json.Serialization;
using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// A specification bound to a slice — the typed <c>Specification</c> slot of
/// jasperfx#687. Stamped by the source that owns the binding (the Bobcat
/// generator, a code-first spec runner), <em>never hand-typed</em>: the identity
/// plus the CLR types the spec's steps resolved is what lets drift colouring read
/// real evidence (no spec → orange, passing → green, failing → red, a spec
/// touching undeclared types → yellow).
/// </summary>
/// <param name="Identity">
///     Stable spec identity in the form <c>{Feature}/{Scenario}</c> — the same key Bobcat
///     publishes on <c>scenario_finished</c>, so run evidence joins on it.
/// </param>
/// <param name="ResolvedTypes">
///     CLR types the specification's steps resolved against the compilation (commands sent,
///     events expected, read models asserted on), in step order, deduplicated. Empty when the
///     binding source cannot resolve types (e.g. a spec bound by identity only).
/// </param>
[method: JsonConstructor]
public sealed record SpecificationDescriptor(
    string Identity,
    IReadOnlyList<TypeDescriptor> ResolvedTypes)
{
    /// <summary>Bind by identity only, with no resolved types.</summary>
    public SpecificationDescriptor(string identity) : this(identity, Array.Empty<TypeDescriptor>())
    {
    }

    /// <summary>The <c>{Feature}</c> half of <see cref="Identity"/>, or the whole identity when it has no separator.</summary>
    [JsonIgnore]
    public string Feature
    {
        get
        {
            var index = Identity.IndexOf('/');
            return index < 0 ? Identity : Identity[..index];
        }
    }

    /// <summary>The <c>{Scenario}</c> half of <see cref="Identity"/>, or null when it has no separator.</summary>
    [JsonIgnore]
    public string? Scenario
    {
        get
        {
            var index = Identity.IndexOf('/');
            return index < 0 ? null : Identity[(index + 1)..];
        }
    }
}
