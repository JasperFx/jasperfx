using System.Reflection;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Declares which handlers and endpoints belong to one domain — a module, in a modular monolith
/// (jasperfx#960). Opened with <see cref="EventModelBuilder.Domain"/>.
/// </summary>
/// <remarks>
/// These are policies: <see cref="DomainAttribute"/> on a handler or method beats any of them, a policy
/// naming a type beats a namespace, and a namespace beats an assembly. <see cref="EventModelDomains"/>
/// applies them, and reports a conflict rather than picking one silently.
/// </remarks>
public sealed class DomainPolicyBuilder
{
    private readonly string _domain;
    private readonly List<DomainAssignmentDescriptor> _assignments;

    internal DomainPolicyBuilder(string domain, List<DomainAssignmentDescriptor> assignments)
    {
        _domain = domain;
        _assignments = assignments;
    }

    /// <summary>Every handler and endpoint in <paramref name="assembly"/> belongs to this domain.</summary>
    public DomainPolicyBuilder Includes(Assembly assembly)
        => add(DomainAssignmentScope.Assembly, assembly.GetName().Name
                                               ?? throw new ArgumentException("The assembly has no name", nameof(assembly)));

    /// <summary>
    /// Every handler and endpoint in <paramref name="namespace"/>, and the namespaces beneath it, belongs
    /// to this domain.
    /// </summary>
    public DomainPolicyBuilder IncludesNamespace(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace))
        {
            throw new ArgumentException("A namespace policy needs a namespace", nameof(@namespace));
        }

        return add(DomainAssignmentScope.Namespace, @namespace.Trim().TrimEnd('.'));
    }

    /// <summary>The namespace of <typeparamref name="TMarker"/>, and the namespaces beneath it, belong to this domain.</summary>
    public DomainPolicyBuilder IncludesNamespaceOf<TMarker>()
        => IncludesNamespace(typeof(TMarker).Namespace
                             ?? throw new ArgumentException($"{typeof(TMarker).Name} has no namespace"));

    /// <summary>The handler or endpoint <typeparamref name="THandler"/> belongs to this domain.</summary>
    public DomainPolicyBuilder Includes<THandler>() => Includes(typeof(THandler));

    /// <summary>The handler or endpoint <paramref name="handlerType"/> belongs to this domain.</summary>
    public DomainPolicyBuilder Includes(Type handlerType)
        => add(DomainAssignmentScope.Type, handlerType.FullName
                                           ?? throw new ArgumentException("The type has no full name", nameof(handlerType)));

    private DomainPolicyBuilder add(DomainAssignmentScope scope, string target)
    {
        var assignment = new DomainAssignmentDescriptor(_domain, scope, target);
        if (!_assignments.Contains(assignment)) _assignments.Add(assignment);
        return this;
    }
}
