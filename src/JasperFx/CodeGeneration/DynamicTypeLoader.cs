using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.CodeGeneration;

/// <summary>
/// <see cref="ITypeLoader"/> that always compiles generated source in-memory
/// using a registered <see cref="IAssemblyGenerator"/>. Requires runtime code
/// generation by definition; the AOT/trim attributes here are the single
/// recognizable seam the toolchain can use to drop the Roslyn pipeline when
/// only <see cref="StaticTypeLoader"/> is registered.
/// </summary>
[RequiresDynamicCode("DynamicTypeLoader compiles C# at runtime via IAssemblyGenerator (Roslyn).")]
[RequiresUnreferencedCode("DynamicTypeLoader emits and loads runtime-generated types that the trimmer cannot statically see.")]
public sealed class DynamicTypeLoader : ITypeLoader
{
    public void Initialize(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        var logger = services?.GetService(typeof(ILogger<ITypeLoader>)) as ILogger ?? NullLogger.Instance;
        var @namespace = parent.ToNamespace(rules);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Generated code for {Namespace}.{FileName}",
                parent.ChildNamespace, file.FileName);
        }

        var generatedAssembly = parent.StartAssembly(rules);
        file.AssembleTypes(generatedAssembly);

        var serviceVariables = parent is ICodeFileCollectionWithServices
            ? services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource
            : null;

        if (serviceVariables != null && file.TryReplaceServiceProvider(out var serviceProvider))
        {
            serviceVariables.ReplaceServiceProvider(serviceProvider);
        }

        var compiler = ResolveAssemblyGenerator(services);
        compiler.Compile(generatedAssembly, serviceVariables);

        if (serviceVariables != null && serviceVariables.ServiceLocations().Any())
        {
            file.AssertServiceLocationsAreAllowed(serviceVariables.ServiceLocations(), services);
        }

        file.AttachTypesSynchronously(rules, generatedAssembly.Assembly!, services, @namespace);
    }

    public async Task InitializeAsync(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        var @namespace = parent.ToNamespace(rules);

        var generatedAssembly = parent.StartAssembly(rules);
        file.AssembleTypes(generatedAssembly);

        var serviceVariables = parent is ICodeFileCollectionWithServices
            ? services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource
            : null;

        var compiler = ResolveAssemblyGenerator(services);
        compiler.Compile(generatedAssembly, serviceVariables);
        await file.AttachTypes(rules, generatedAssembly.Assembly!, services, @namespace);
    }

    /// <summary>
    /// Compile the <see cref="ICodeFile"/> in-memory and additionally return the
    /// generated source so callers can write it to disk (the Auto / Static
    /// fallback path).
    /// </summary>
    internal static void CompileAndAttach(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services,
        out string code)
    {
        var @namespace = parent.ToNamespace(rules);

        var generatedAssembly = parent.StartAssembly(rules);
        file.AssembleTypes(generatedAssembly);

        var serviceVariables = services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource;
        if (serviceVariables != null && file.TryReplaceServiceProvider(out var serviceProvider))
        {
            serviceVariables.ReplaceServiceProvider(serviceProvider);
        }

        var compiler = ResolveAssemblyGenerator(services);
        compiler.Compile(generatedAssembly, serviceVariables, out code);

        if (serviceVariables != null && serviceVariables.ServiceLocations().Any())
        {
            file.AssertServiceLocationsAreAllowed(serviceVariables.ServiceLocations(), services);
        }

        file.AttachTypesSynchronously(rules, generatedAssembly.Assembly!, services, @namespace);
    }

    /// <summary>
    /// Async variant of <see cref="CompileAndAttach"/>.
    /// </summary>
    internal static async Task<string> CompileAndAttachAsync(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        var @namespace = parent.ToNamespace(rules);

        var generatedAssembly = parent.StartAssembly(rules);
        file.AssembleTypes(generatedAssembly);

        var serviceVariables = services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource;

        var compiler = ResolveAssemblyGenerator(services);
        compiler.Compile(generatedAssembly, serviceVariables, out var code);

        await file.AttachTypes(rules, generatedAssembly.Assembly!, services, @namespace);

        return code;
    }

    private static IAssemblyGenerator ResolveAssemblyGenerator(IServiceProvider? services)
    {
        var generator = services?.GetService(typeof(IAssemblyGenerator)) as IAssemblyGenerator;
        if (generator != null) return generator;

        throw new InvalidOperationException(
            "No IAssemblyGenerator is registered in the application's service provider, but runtime code generation was requested. " +
            "Either: (a) install the JasperFx.RuntimeCompiler package and call services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>() to enable runtime Roslyn compilation, " +
            "or (b) pre-generate all code (typically with 'dotnet run -- codegen write') and set GenerationRules.TypeLoadMode = TypeLoadMode.Static so runtime compilation is never invoked.");
    }
}
