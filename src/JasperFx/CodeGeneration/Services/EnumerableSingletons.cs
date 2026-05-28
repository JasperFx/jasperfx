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
    /// singleton instance.
    /// <para>
    /// The factory binds directly to <paramref name="source"/> (its <see cref="ServiceDescriptor.ImplementationInstance"/>,
    /// <see cref="ServiceDescriptor.ImplementationFactory"/>, or <see cref="ServiceDescriptor.ImplementationType"/>)
    /// rather than calling <c>sp.GetServices(elementType)</c>. The latter would re-enter the same
    /// <c>IEnumerable&lt;T&gt;</c> resolution while the container is inlining the singleton element of
    /// that enumerable, creating infinite recursion in code-gen containers that inline singleton values
    /// (e.g. Lamar). Binding to the source descriptor short-circuits that cycle.
    /// </para>
    /// </summary>
    public static ServiceDescriptor KeyedMirror(Type elementType, int nonKeyedOrdinal, ServiceDescriptor source)
    {
        if (source.ImplementationInstance is not null)
        {
            var instance = source.ImplementationInstance;
            return new ServiceDescriptor(elementType, KeyFor(nonKeyedOrdinal),
                (_, _) => instance, ServiceLifetime.Singleton);
        }

        if (source.ImplementationFactory is not null)
        {
            var factory = source.ImplementationFactory;
            return new ServiceDescriptor(elementType, KeyFor(nonKeyedOrdinal),
                (sp, _) => factory(sp), ServiceLifetime.Singleton);
        }

        var implementationType = source.ImplementationType
            ?? throw new InvalidOperationException(
                $"Source ServiceDescriptor for {elementType.FullNameInCode()} (ordinal {nonKeyedOrdinal}) " +
                "has neither an implementation instance, factory, nor type.");

        return new ServiceDescriptor(elementType, KeyFor(nonKeyedOrdinal),
            (sp, _) => ActivatorUtilities.CreateInstance(sp, implementationType), ServiceLifetime.Singleton);
    }
}
