using JasperFx.Core.Reflection;
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
    /// <summary>
    /// Prefix shared by every keyed mirror registration. The full key is this prefix followed by the
    /// element's non-keyed ordinal within its service family.
    /// </summary>
    public const string KeyPrefix = "jasperfx-enumerable-singleton-";

    // Stable key per non-keyed registration ordinal within a service family. Keyed services are
    // scoped by (ServiceType, key), so uniqueness of the ordinal within the family is sufficient.
    public static string KeyFor(int nonKeyedOrdinal) => KeyPrefix + nonKeyedOrdinal;

    /// <summary>
    /// True when <paramref name="key"/> is one of the keyed mirror keys minted by <see cref="KeyFor"/>.
    /// Used by the generated enumerable code to decide which singleton elements need a missing-mirror
    /// null guard.
    /// </summary>
    public static bool IsMirrorKey(object? key) =>
        key is string s && s.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Actionable message thrown by the generated enumerable guard when a keyed mirror is missing —
    /// i.e. the singleton element resolved to <see langword="null"/> because
    /// <see cref="JasperFx.EnumerableSingletonRegistrationExtensions.AddJasperFxEnumerableSingletonSupport"/>
    /// was never called (or ran before the mixed-lifetime family was registered).
    /// </summary>
    public static string MissingMirrorMessage(Type elementType, object? key) =>
        $"Keyed mirror '{key}' for the singleton element of IEnumerable<{elementType.FullNameInCode()}> was not found. " +
        "Call AddJasperFxEnumerableSingletonSupport() once during DI setup, before BuildServiceProvider().";

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
