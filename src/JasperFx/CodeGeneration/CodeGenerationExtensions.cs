using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class MissingTypeException : Exception
{
    public MissingTypeException(string? message) : base(message)
    {
    }
}

public static class CodeGenerationExtensions
{
    // Per-assembly cache of pre-generated types indexed by full name. Used by
    // FindPreGeneratedType so static-mode code-file attachment does not pay an
    // O(ExportedTypes) scan per lookup. ConditionalWeakTable means we don't
    // root assemblies that the host might unload (plugin scenarios).
    private static readonly ConditionalWeakTable<Assembly, IReadOnlyDictionary<string, Type>> _exportedTypeIndex
        = new();

    /// <summary>
    ///     Try to locate a pre-generated type by namespace and type name in the
    ///     supplied assembly
    /// </summary>
    /// <param name="assembly"></param>
    /// <param name="namespace"></param>
    /// <param name="typeName"></param>
    /// <returns></returns>
    public static Type? FindPreGeneratedType(this Assembly assembly, string @namespace, string typeName)
    {
        var fullName = $"{@namespace}.{typeName}";
        var index = _exportedTypeIndex.GetValue(assembly, BuildExportedTypeIndex);
        return index.TryGetValue(fullName, out var type) ? type : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Indexes exported types of an assembly already loaded into the AppDomain. Used by FindPreGeneratedType to locate codegen output — types that aren't preserved by the consuming app's static graph wouldn't be reachable from FindPreGeneratedType callers anyway.")]
    private static IReadOnlyDictionary<string, Type> BuildExportedTypeIndex(Assembly assembly)
    {
        // ExportedTypes can contain duplicates only in pathological cases (forwarded
        // types crossing module boundaries with name conflicts). Use the first
        // occurrence to mirror the historical FirstOrDefault behavior.
        var dict = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in assembly.ExportedTypes)
        {
            var fullName = type.FullName;
            if (fullName != null && !dict.ContainsKey(fullName))
            {
                dict[fullName] = type;
            }
        }

        return dict;
    }

    /// <summary>
    ///     Says why expected pre-generated types are missing from <paramref name="assembly" /> and what to do
    ///     about it, for a failure that has already established a type was expected. jasperfx#1041: "nothing
    ///     generated is here at all" and "this one type is missing" have different causes and different fixes,
    ///     and only the first can mean ILC trimmed the code away. An application with no generated code never
    ///     gets here, because no <see cref="ICodeFile" /> ever asked for a type.
    /// </summary>
    internal static string DescribeMissingPreGeneratedTypes(this Assembly assembly, GenerationRules rules)
        => assembly.DescribeMissingPreGeneratedTypes(rules, RuntimeFeature.IsDynamicCodeSupported);

    // Split for testability, the same way GenerationRules.DefaultLoaderFor is: the dynamic-code-unsupported
    // branch cannot be reached from a CoreCLR test process.
    internal static string DescribeMissingPreGeneratedTypes(this Assembly assembly, GenerationRules rules,
        bool dynamicCodeSupported)
    {
        var @namespace = rules.GeneratedNamespace;
        var assemblyName = assembly.GetName().Name;
        var codegenWrite = "'dotnet run -- codegen write'";
        var rooting =
            $"{nameof(AotRootsCompanionExtensions.AddAotRoots)} on the {nameof(GeneratedAssembly)}, or hand-written [DynamicDependency] roots for F# output. See 'Rooting pre-generated types' in the JasperFx AOT guide.";

        var prefix = @namespace + ".";
        var index = _exportedTypeIndex.GetValue(assembly, BuildExportedTypeIndex);
        if (index.Keys.Any(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var stale =
                $"Other pre-generated types are present under namespace '{@namespace}' in '{assemblyName}', so the generated code is out of date: run {codegenWrite} again and rebuild.";

            return dynamicCodeSupported
                ? stale
                : $"{stale} In a Native AOT image the missing type may also have been trimmed: the generated code must root it with {rooting}";
        }

        if (dynamicCodeSupported)
        {
            return
                $"{nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)} found no pre-generated types at all under namespace '{@namespace}' in '{assemblyName}', and it has no fallback. " +
                $"Run {codegenWrite} and compile the output in '{rules.GeneratedCodeOutputPath}' into '{assemblyName}', " +
                $"point {nameof(GenerationRules)}.{nameof(GenerationRules.ApplicationAssembly)} at the assembly that holds it, " +
                $"or use {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Auto)} to generate missing types at runtime.";
        }

        return
            $"This Native AOT image has no pre-generated types at all under namespace '{@namespace}' in '{assemblyName}'. " +
            $"Run {codegenWrite} and compile the output in '{rules.GeneratedCodeOutputPath}' into '{assemblyName}' before publishing. " +
            $"If that output is already compiled in, ILC trimmed it, because generated code is only reached by reflection: root it with {rooting}";
    }

    public static GeneratedAssembly StartAssembly(this ICodeFileCollection generator, GenerationRules rules)
    {
        if (generator.ChildNamespace.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Missing {nameof(ICodeFileCollection.ChildNamespace)} for {generator}");
        }

        var @namespace = $"{rules.GeneratedNamespace}.{generator.ChildNamespace}";

        return new GeneratedAssembly(rules, @namespace);
    }

    public static string ToNamespace(this ICodeFileCollection codeFileCollection, GenerationRules rules)
    {
        return $"{rules.GeneratedNamespace}.{codeFileCollection.ChildNamespace}";
    }

    public static string ToExportDirectory(this ICodeFileCollection generator, string exportDirectory)
    {
        if (generator.ChildNamespace.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Missing {nameof(ICodeFileCollection.ChildNamespace)} for {generator}");
        }

        var generatorDirectory = exportDirectory;
        var parts = generator.ChildNamespace.Split('.');
        foreach (var part in parts) generatorDirectory = Path.Combine(generatorDirectory, part);

        FileSystem.CreateDirectoryIfNotExists(generatorDirectory);

        return generatorDirectory;
    }

    public static GeneratedAssembly AssembleTypes(this ICodeFileCollection generator, GenerationRules rules)
    {
        var generatedAssembly = generator.StartAssembly(rules);
        foreach (var file in generator.BuildFiles()) file.AssembleTypes(generatedAssembly);

        return generatedAssembly;
    }

    /// <summary>
    ///     Add a new string constant to the generated type
    /// </summary>
    /// <param name="generatedType"></param>
    /// <param name="constantName"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static Setter AddStringConstant(this GeneratedType generatedType, string constantName, string value)
    {
        var setter = Setter.Constant(constantName, Constant.ForString(value));
        generatedType.Setters.Add(setter);

        return setter;
    }

    /// <summary>
    ///     Creates a new string constant value on the holding type and uses that value as the return
    ///     value for this method
    /// </summary>
    /// <param name="frames"></param>
    /// <param name="constantName"></param>
    /// <param name="value"></param>
    public static void ReturnNewStringConstant(this FramesCollection frames,
        string constantName, string value)
    {
        var setter = frames.ParentMethod.ParentType.AddStringConstant(constantName, value);
        frames.Return(setter);
    }

    /// <summary>
    ///     Assert that all the expected pre-generated types exist in the configured assembly
    /// </summary>
    /// <param name="collection"></param>
    /// <param name="services"></param>
    /// <exception cref="MissingTypeException"></exception>
    public static void AssertPreBuildTypesExist(this ICodeFileCollection collection, IServiceProvider services)
    {
        var missing = new List<ICodeFile>();

        foreach (var file in collection.BuildFiles())
        {
            var @namespace = $"{collection.Rules.GeneratedNamespace}.{collection.ChildNamespace}";
            if (!file.AttachTypesSynchronously(collection.Rules, collection.Rules.ApplicationAssembly, services,
                    @namespace))
            {
                missing.Add(file);
            }
        }

        if (missing.Any())
        {
            throw new MissingTypeException(
                $"Missing expected pre-generated type(s) from assembly {collection.Rules.ApplicationAssembly.FullName}:\n" +
                missing.Select(x => x.ToString())!.Join("\n") + "\n" +
                collection.Rules.ApplicationAssembly.DescribeMissingPreGeneratedTypes(collection.Rules));
        }
    }
}
