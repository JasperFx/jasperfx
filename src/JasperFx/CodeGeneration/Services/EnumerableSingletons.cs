using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

/// <summary>
/// Support for inline-generating <c>IEnumerable&lt;T&gt;</c> dependencies whose element registrations
/// mix DI lifetimes. A singleton element cannot be injected by its (ambiguous) service type when the
/// type has several registrations, so it is injected via a keyed "mirror" registration that forwards
/// to — and therefore shares — the original non-keyed singleton instance. The generated constructor
/// parameter uses <c>[FromKeyedServices(key)]</c> to pull that specific instance.
/// </summary>
internal static class EnumerableSingletons
{
    // Stable key per non-keyed registration ordinal within a service family. Keyed services are
    // scoped by (ServiceType, key), so uniqueness of the ordinal within the family is sufficient.
    public static string KeyFor(int nonKeyedOrdinal) => "jasperfx-enumerable-singleton-" + nonKeyedOrdinal;

    /// <summary>
    /// A keyed singleton descriptor that forwards to the <paramref name="nonKeyedOrdinal"/>-th
    /// non-keyed registration of <paramref name="elementType"/>, sharing that registration's
    /// singleton instance (GetServices honors each registration's own lifetime).
    /// </summary>
    public static ServiceDescriptor KeyedMirror(Type elementType, int nonKeyedOrdinal)
    {
        var ordinal = nonKeyedOrdinal;
        return new ServiceDescriptor(elementType, KeyFor(ordinal),
            (sp, _) => sp.GetServices(elementType).ElementAt(ordinal)!, ServiceLifetime.Singleton);
    }
}
