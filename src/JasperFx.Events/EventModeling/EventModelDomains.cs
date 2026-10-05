using System.Reflection;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Puts a handler or HTTP endpoint — the class, or a single handler method — in a domain: in a modular
/// monolith, a module (jasperfx#960). The most specific way to declare a domain, beating every policy
/// declared with <c>EventModelBuilder.Domain(name)</c>; a method's attribute beats its class's.
/// </summary>
/// <example>
/// <code>
/// [Domain("Billing")]
/// public class OrderPlacedHandler
/// {
///     public InvoiceRequested Handle(OrderPlaced e) => new(e.OrderId);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DomainAttribute : Attribute
{
    /// <summary>Put the decorated handler or endpoint in <paramref name="name"/>.</summary>
    /// <param name="name">The domain (module) name.</param>
    public DomainAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A domain needs a name", nameof(name));
        Name = name;
    }

    /// <summary>The domain (module) name.</summary>
    public string Name { get; }
}

/// <summary>Where a resolved domain came from, most specific first (jasperfx#960).</summary>
public enum DomainResolutionSource
{
    /// <summary>Nothing declared a domain for the handler.</summary>
    None,

    /// <summary><see cref="DomainAttribute"/> on the handler method.</summary>
    MethodAttribute,

    /// <summary><see cref="DomainAttribute"/> on the handler or endpoint class.</summary>
    TypeAttribute,

    /// <summary>A policy naming the handler type itself.</summary>
    TypePolicy,

    /// <summary>A policy naming the handler's namespace, or one above it — the longest match wins.</summary>
    NamespacePolicy,

    /// <summary>A policy naming the handler's assembly.</summary>
    AssemblyPolicy,
}

/// <summary>
/// The domain a handler or endpoint resolved to, where that came from, and every <em>other</em> domain
/// some declaration claimed for it (jasperfx#960).
/// </summary>
/// <param name="Domain">
///     The winning domain, or null when nothing declared one — or when the most specific declarations
///     tie on different domains, which is <see cref="IsAmbiguous"/> and never guessed.
/// </param>
/// <param name="Source">Which kind of declaration decided it.</param>
/// <param name="Conflicts">
///     The other domains claimed for the same handler, in precedence order. A policy overridden by an
///     attribute is a <em>conflict</em> worth showing someone, not an error: the attribute still wins.
/// </param>
public sealed record DomainResolution(string? Domain, DomainResolutionSource Source, IReadOnlyList<string> Conflicts)
{
    /// <summary>The most specific declarations disagreed, so no domain was chosen.</summary>
    public bool IsAmbiguous => Domain is null && Source != DomainResolutionSource.None;

    /// <summary>Nothing declared a domain.</summary>
    public static readonly DomainResolution Undeclared = new(null, DomainResolutionSource.None, Array.Empty<string>());
}

/// <summary>
/// Applies declared domains — <see cref="DomainAttribute"/> and the
/// <see cref="DomainAssignmentDescriptor"/> policies on an Event Model — to a handler or endpoint
/// (jasperfx#960). This is what the sources that derive slices from code call, so a modular monolith's
/// modules become each slice's <see cref="EventModelSliceDescriptor.Domain"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared, never inferred.</b> A handler nothing declared stays undeclared; this never guesses a
/// domain from a namespace or an assembly name on its own.
/// </para>
/// <para>
/// <b>Precedence</b>, most specific first: <see cref="DomainAttribute"/> on the method, on the class, a
/// policy naming the type, a namespace policy (the longest matching namespace), an assembly policy. Two
/// declarations at the same level naming <em>different</em> domains are ambiguous: no domain is chosen and
/// both are reported, rather than one being picked by order.
/// </para>
/// </remarks>
public static class EventModelDomains
{
    /// <summary>
    /// Resolve the domain of <paramref name="handlerType"/>, optionally for one of its
    /// <paramref name="handlerMethod"/>s, against <paramref name="assignments"/> — typically every
    /// assembled model's <see cref="EventModelDescriptor.DomainAssignments"/>,
    /// <c>models.SelectMany(x =&gt; x.DomainAssignments)</c>.
    /// </summary>
    public static DomainResolution Resolve(Type handlerType, MethodInfo? handlerMethod,
        IEnumerable<DomainAssignmentDescriptor> assignments)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        var policies = assignments as IReadOnlyCollection<DomainAssignmentDescriptor> ?? assignments.ToList();

        // Every level, most specific first; each level holds the distinct domains it claims.
        var levels = new List<(DomainResolutionSource Source, IReadOnlyList<string> Domains)>
        {
            (DomainResolutionSource.MethodAttribute, attributeOn(handlerMethod)),
            (DomainResolutionSource.TypeAttribute, attributeOn(handlerType)),
            (DomainResolutionSource.TypePolicy, matching(policies, DomainAssignmentScope.Type,
                x => string.Equals(x.Target, handlerType.FullName, StringComparison.Ordinal))),
            (DomainResolutionSource.NamespacePolicy, longestNamespace(policies, handlerType.Namespace)),
            (DomainResolutionSource.AssemblyPolicy, matching(policies, DomainAssignmentScope.Assembly,
                x => string.Equals(x.Target, handlerType.Assembly.GetName().Name, StringComparison.Ordinal))),
        };

        var deciding = levels.FirstOrDefault(x => x.Domains.Count > 0);
        if (deciding.Domains is null) return DomainResolution.Undeclared;

        var domain = deciding.Domains.Count == 1 ? deciding.Domains[0] : null;

        // Every claim made at any level -- including a shallower namespace a deeper one overrode -- so a
        // reader sees each declaration that disagreed with the answer, not just the ones that tied.
        var conflicts = levels
            .SelectMany(x => x.Domains)
            .Concat(coveringNamespaces(policies, handlerType.Namespace).Select(x => x.Domain))
            .Where(x => !string.Equals(x, domain, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new DomainResolution(domain, deciding.Source, conflicts);
    }

    private static IReadOnlyList<string> attributeOn(MemberInfo? member)
        => member?.GetCustomAttribute<DomainAttribute>() is { } attribute ? [attribute.Name] : Array.Empty<string>();

    private static IReadOnlyList<string> matching(IEnumerable<DomainAssignmentDescriptor> policies,
        DomainAssignmentScope scope, Func<DomainAssignmentDescriptor, bool> applies)
        => policies.Where(x => x.Scope == scope && applies(x)).Select(x => x.Domain)
            .Distinct(StringComparer.Ordinal).ToList();

    // The deepest declared namespace containing the handler's wins; "MyApp.Billing" covers
    // "MyApp.Billing.Invoices" but not "MyApp.BillingReports".
    private static IReadOnlyList<string> longestNamespace(IEnumerable<DomainAssignmentDescriptor> policies,
        string? handlerNamespace)
    {
        var covering = coveringNamespaces(policies, handlerNamespace);
        if (covering.Count == 0) return Array.Empty<string>();

        var deepest = covering.Max(x => x.Target.Length);
        return covering.Where(x => x.Target.Length == deepest).Select(x => x.Domain)
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<DomainAssignmentDescriptor> coveringNamespaces(
        IEnumerable<DomainAssignmentDescriptor> policies, string? handlerNamespace)
        => string.IsNullOrEmpty(handlerNamespace)
            ? Array.Empty<DomainAssignmentDescriptor>()
            : policies
                .Where(x => x.Scope == DomainAssignmentScope.Namespace
                            && (string.Equals(handlerNamespace, x.Target, StringComparison.Ordinal)
                                || handlerNamespace.StartsWith(x.Target + ".", StringComparison.Ordinal)))
                .ToList();
}
