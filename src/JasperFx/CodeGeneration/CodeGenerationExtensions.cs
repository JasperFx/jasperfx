using System.Collections.Concurrent;
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
                missing.Select(x => x.ToString())!.Join("\n"));
        }
    }
}
