using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx;

/// <summary>
/// Registration support for inline code-generation of <c>IEnumerable&lt;T&gt;</c> dependencies whose
/// elements mix DI lifetimes (e.g. one singleton + one scoped registration of the same service type).
/// </summary>
public static class EnumerableSingletonRegistrationExtensions
{
    /// <summary>
    /// For every service family that mixes a singleton with non-singleton registrations, register a
    /// keyed "mirror" singleton per singleton element that forwards to — and so shares — the original
    /// non-keyed singleton instance. JasperFx's generated <c>IEnumerable&lt;T&gt;</c> code injects each
    /// such singleton via <c>[FromKeyedServices(key)]</c> on the generated constructor, which lets the
    /// container hand back the specific singleton among multiple registrations of the same type.
    /// <para>
    /// Call this once during DI setup <b>before</b> <c>BuildServiceProvider()</c>. It is idempotent.
    /// Families that are all-singleton or all-non-singleton are left untouched.
    /// </para>
    /// </summary>
    public static IServiceCollection AddJasperFxEnumerableSingletonSupport(this IServiceCollection services)
    {
        var families = services
            .Where(d => !d.IsKeyedService)
            .GroupBy(d => d.ServiceType)
            .ToList();

        var toAdd = new List<ServiceDescriptor>();

        foreach (var family in families)
        {
            var nonKeyed = family.ToList();

            var hasSingleton = nonKeyed.Any(d => d.Lifetime == ServiceLifetime.Singleton);
            var hasOther = nonKeyed.Any(d => d.Lifetime != ServiceLifetime.Singleton);

            // Only mixed-lifetime families need keyed mirrors. All-singleton enumerables are built as
            // a single shared field; all-non-singleton enumerables are fully inline.
            if (!(hasSingleton && hasOther))
            {
                continue;
            }

            for (var i = 0; i < nonKeyed.Count; i++)
            {
                if (nonKeyed[i].Lifetime != ServiceLifetime.Singleton)
                {
                    continue;
                }

                var key = EnumerableSingletons.KeyFor(i);
                var alreadyRegistered = services.Any(d =>
                    d.IsKeyedService && d.ServiceType == family.Key && Equals(d.ServiceKey, key));

                if (!alreadyRegistered)
                {
                    toAdd.Add(EnumerableSingletons.KeyedMirror(family.Key, i));
                }
            }
        }

        foreach (var descriptor in toAdd)
        {
            services.Add(descriptor);
        }

        return services;
    }
}
