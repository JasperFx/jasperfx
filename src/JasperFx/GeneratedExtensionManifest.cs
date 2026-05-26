using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx;

/// <summary>
/// Reads the source-generated <c>JasperFx.Generated.DiscoveredExtensions</c> manifests that
/// <c>JasperFx.SourceGenerator</c> emits into eligible assemblies ([JasperFxAssembly]-marked or
/// executable). Lets consuming frameworks discover their extension/option types without the
/// filesystem-probing assembly scan (AssemblyFinder), then filter by their own extension
/// interface (e.g. IServiceRegistrations, Wolverine's IWolverineExtension).
/// </summary>
public static class GeneratedExtensionManifest
{
    private const string ManifestTypeName = "JasperFx.Generated.DiscoveredExtensions";
    private const string ManifestPropertyName = "ExtensionTypes";
    private const string RegistrationsTypeName = "JasperFx.Generated.GeneratedServiceRegistrations";
    private const string RegistrationsMethodName = "Register";

    /// <summary>
    /// All extension types discovered at compile time across the supplied assemblies, deduplicated.
    /// </summary>
    public static IReadOnlyList<Type> ReadFrom(IEnumerable<Assembly> assemblies)
    {
        var results = new List<Type>();
        var seen = new HashSet<Type>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in ReadFromAssembly(assembly))
            {
                if (seen.Add(type))
                {
                    results.Add(type);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// All compile-time-discovered extension types across the currently loaded, non-dynamic
    /// assemblies. This avoids the filesystem-probing assembly scan; if no manifests are present
    /// (the source generator wasn't run), it simply returns an empty list and the caller should
    /// fall back to its reflective discovery.
    /// </summary>
    public static IReadOnlyList<Type> ReadFromLoadedAssemblies()
    {
        return ReadFrom(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic));
    }

    /// <summary>
    /// True if any loaded assembly carries a source-generated extension manifest, i.e. the
    /// generator is active and discovery results can be trusted without reflective scanning.
    /// </summary>
    public static bool AnyManifestPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Any(a => a.GetType(ManifestTypeName) != null);
    }

    /// <summary>
    /// Applies every source-generated DI registration discovered across the currently loaded,
    /// non-dynamic assemblies by invoking each assembly's generated
    /// <c>JasperFx.Generated.GeneratedServiceRegistrations.Register(IServiceCollection)</c> method.
    /// The generated registration code is reflection-free; if no registrations are present this is a
    /// no-op and the caller should fall back to its own registration path.
    /// </summary>
    public static void RegisterAllDiscoveredServices(IServiceCollection services)
    {
        RegisterAllDiscoveredServices(services,
            AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic));
    }

    /// <summary>
    /// Applies the source-generated DI registrations contained in the supplied assemblies.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JasperFx.Generated.GeneratedServiceRegistrations is emitted by JasperFx.SourceGenerator as ordinary code in the consuming assembly and survives trimming; absence is a no-op.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GeneratedServiceRegistrations.Register is a source-generated public static method in the consuming assembly.")]
    public static void RegisterAllDiscoveredServices(IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            var method = assembly.GetType(RegistrationsTypeName)?.GetMethod(
                RegistrationsMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(IServiceCollection) },
                modifiers: null);

            method?.Invoke(null, new object[] { services });
        }
    }

    /// <summary>
    /// True if any loaded, non-dynamic assembly carries source-generated DI registrations, i.e. the
    /// generator is active and <see cref="RegisterAllDiscoveredServices(IServiceCollection)"/> will do work.
    /// </summary>
    public static bool AnyServiceRegistrationsPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Any(a => a.GetType(RegistrationsTypeName) != null);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JasperFx.Generated.DiscoveredExtensions is emitted by JasperFx.SourceGenerator as ordinary code in the consuming assembly and survives trimming; absence degrades to an empty result.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "ExtensionTypes is a source-generated static property in the consuming assembly.")]
    private static IEnumerable<Type> ReadFromAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            yield break;
        }

        var manifestType = assembly.GetType(ManifestTypeName);
        var property = manifestType?.GetProperty(ManifestPropertyName, BindingFlags.Public | BindingFlags.Static);
        if (property?.GetValue(null) is not IEnumerable<Type> types)
        {
            yield break;
        }

        foreach (var type in types)
        {
            yield return type;
        }
    }
}
