using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Points at the type a spec generator writes into a specification assembly: its static <c>All</c> property
/// lists every specification as a <see cref="SpecificationBindingDescriptor" />. Read it with
/// <see cref="SpecificationManifest.In" />. Generated; never written by hand (jasperfx#997).
/// </summary>
/// <remarks>
/// In JasperFx, beside the descriptor it lists, so a model command can read a manifest without referencing
/// the tool that wrote it. Bobcat writes it (bobcat#449).
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SpecificationManifestAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)]
    Type manifestType) : Attribute
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)]
    public Type ManifestType { get; } = manifestType;
}

/// <summary>
/// Reads the specification bindings a spec assembly carries, for <see cref="EventModelSpecifications.Link" />
/// to join onto an assembled Event Model (jasperfx#997).
/// </summary>
public static class SpecificationManifest
{
    // Bobcat's own copy of the attribute, written by Bobcat versions that predate this one. Matched by
    // name so JasperFx never references Bobcat.
    private const string BobcatAttributeName = "Bobcat.SpecificationManifestAttribute";

    /// <summary>
    /// The bindings written into <paramref name="assembly" />, through either JasperFx's
    /// <see cref="SpecificationManifestAttribute" /> or Bobcat's older one; empty when it carries none.
    /// </summary>
    [RequiresUnreferencedCode("Reads the generated manifest type's static All property by reflection")]
    public static IReadOnlyList<SpecificationBindingDescriptor> In(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var manifestType = assembly.GetCustomAttribute<SpecificationManifestAttribute>()?.ManifestType
                           ?? assembly.GetCustomAttributesData()
                               .Where(x => x.AttributeType.FullName == BobcatAttributeName)
                               .Select(x => x.ConstructorArguments.FirstOrDefault().Value as Type)
                               .FirstOrDefault(x => x != null);

        return manifestType?
            .GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null) as IReadOnlyList<SpecificationBindingDescriptor> ?? [];
    }

    /// <summary>
    /// Load the specification assembly at <paramref name="assemblyPath" /> and read its bindings, for a
    /// model command handed <c>--specs</c>. The assembly is loaded into the default context so it shares
    /// this process's JasperFx (and the application's own types); its other dependencies resolve from its
    /// own <c>.deps.json</c> and folder.
    /// </summary>
    [RequiresUnreferencedCode("Loads an assembly from disk and reads its generated manifest by reflection")]
    public static IReadOnlyList<SpecificationBindingDescriptor> Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No specification assembly at '{fullPath}'", fullPath);
        }

        var resolver = new AssemblyDependencyResolver(fullPath);

        Assembly? resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = resolver.ResolveAssemblyToPath(name);
            return path == null ? null : context.LoadFromAssemblyPath(path);
        }

        AssemblyLoadContext.Default.Resolving += resolve;
        try
        {
            // Already loaded under the same identity (e.g. the spec assembly is the running one): reuse it,
            // since loading one identity twice into the default context throws.
            var identity = AssemblyName.GetAssemblyName(fullPath).FullName;
            var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(x => x.FullName == identity)
                           ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);

            return In(assembly);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= resolve;
        }
    }
}
