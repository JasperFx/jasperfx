namespace JasperFx.Events.EventModeling;

/// <summary>
/// What a <see cref="DomainAssignmentDescriptor"/> covers (jasperfx#960).
/// </summary>
/// <remarks>
/// Ordered from the broadest scope to the narrowest. Goes over the wire as an integer under the
/// System.Text.Json defaults, so new members are appended, never slotted in.
/// </remarks>
public enum DomainAssignmentScope
{
    /// <summary>Every handler and endpoint in an assembly, by the assembly's simple name.</summary>
    Assembly,

    /// <summary>Every handler and endpoint in a namespace and the namespaces beneath it.</summary>
    Namespace,

    /// <summary>One handler or endpoint type, by its full name.</summary>
    Type,
}

/// <summary>
/// A declared policy putting handlers and endpoints in a domain — in a modular monolith, a module
/// (jasperfx#960). Declared through <c>EventModelBuilder.Domain(name)</c> and carried on
/// <see cref="EventModelDescriptor.DomainAssignments"/>, so the sources that derive slices from code
/// (Wolverine's handler, HTTP and gRPC chains) can give each slice its domain.
/// </summary>
/// <remarks>
/// Module membership is <b>declared, never inferred</b>: nothing in the Critter Stack guesses a domain
/// from a namespace or an assembly on its own. Policies like these, and the <c>[Domain]</c> attribute on
/// a handler or endpoint, are the only inputs; <c>EventModelDomains.Resolve</c> applies them.
/// </remarks>
/// <param name="Domain">The domain (module) the target belongs to.</param>
/// <param name="Scope">Whether <paramref name="Target"/> names an assembly, a namespace or a type.</param>
/// <param name="Target">The assembly's simple name, the namespace, or the type's full name.</param>
public sealed record DomainAssignmentDescriptor(string Domain, DomainAssignmentScope Scope, string Target);
