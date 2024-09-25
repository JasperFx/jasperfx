using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class GeneratedAssembly
{
    private readonly IList<Assembly> _assemblies = new List<Assembly>();
    private readonly List<GeneratedType> _generatedTypes = new();

    public GeneratedAssembly(GenerationRules rules)
    {
        Rules = rules;
        Namespace = rules.GeneratedNamespace;
    }

    public GeneratedAssembly(GenerationRules rules, string ns)
    {
        Namespace = ns;
        Rules = rules;
    }

    public IReadOnlyList<GeneratedType> GeneratedTypes => _generatedTypes;

    public string Namespace { get; }

    public GenerationRules Rules { get; }

    public Assembly? Assembly { get; private set; }

    /// <summary>
    ///     Optional code fragment to write at the beginning of this
    ///     code file
    /// </summary>
    public ICodeFragment? Header { get; set; }

    /// <summary>
    ///     Optional code fragment to write at the end of this code file
    /// </summary>
    public ICodeFragment? Footer { get; set; }

    /// <summary>
    ///     Extra namespaces to be written out as using blocks
    ///     in the generated code
    /// </summary>
    public IList<string> UsingNamespaces { get; } = new List<string>();

    public void ReferenceAssembly(Assembly assembly)
    {
        _assemblies.Fill(assembly);
    }

    public GeneratedType AddType(string typeName, Type baseType)
    {
        if (Assembly != null)
        {
            throw new InvalidOperationException("This generated assembly has already been compiled");
        }

        var generatedType = new GeneratedType(this, typeName);
        if (baseType.IsInterface)
        {
            generatedType.Implements(baseType);
        }
        else
        {
            generatedType.InheritsFrom(baseType);
        }

        generatedType.ParentAssembly = this;
        _generatedTypes.Add(generatedType);

        return generatedType;
    }

    public void AttachAssembly(Assembly assembly)
    {
        var generated = assembly.GetExportedTypes().ToArray();

        foreach (var generatedType in GeneratedTypes) generatedType.FindType(generated);

        Assembly = assembly;
    }

    public string GenerateCode(IServiceVariableSource? services = null)
    {
        foreach (var generatedType in GeneratedTypes)
        {
            services?.StartNewType();
            generatedType.ArrangeFrames(services);
        }

        var namespaces = AllReferencedNamespaces();

        using var writer = new SourceWriter();
        Header?.Write(writer);
        writer.WriteLine("// <auto-generated/>");

        // Disable all warnings per user request
        writer.WriteLine("#pragma warning disable");

        foreach (var ns in namespaces.OrderBy(x => x)) writer.Write($"using {ns};");

        writer.BlankLine();

        writer.Namespace(Namespace);

        foreach (var @class in GeneratedTypes)
        {
            writer.WriteLine($"// START: {@class.TypeName}");
            @class.Write(writer);
            writer.WriteLine($"// END: {@class.TypeName}");

            writer.WriteLine("");
            writer.WriteLine("");
        }

        writer.FinishBlock();

        Footer?.Write(writer);


        var code = writer.Code();

        attachSourceCodeToTypes(ref code);

        return code;
    }

    public List<string> AllReferencedNamespaces()
    {
        var namespaces = GeneratedTypes
            .SelectMany(x => x.AllInjectedFields)
            .Select(x => x.ArgType.Namespace)
            .Concat(UsingNamespaces)
            .Distinct()
            .Where(x => x.IsNotEmpty()) // weed out blank namespaces, thank you F#!
            .ToList();
        return namespaces!;
    }

    private void attachSourceCodeToTypes(ref string code)
    {
        using (var parser = new SourceCodeParser(code))
        {
            foreach (var type in GeneratedTypes) type.SourceCode = parser.CodeFor(type.TypeName);
        }
    }

    /// <summary>
    ///     Creates a new GeneratedAssembly with default generation
    ///     rules and using the namespace "LamarGenerated"
    /// </summary>
    /// <returns></returns>
    public static GeneratedAssembly Empty()
    {
        return new GeneratedAssembly(new GenerationRules("LamarGenerated"));
    }

    public IEnumerable<Assembly> AllReferencedAssemblies()
    {
        return _generatedTypes.SelectMany(x => x.AllReferencedAssemblies())
            .Concat(_assemblies).Distinct();
    }
}