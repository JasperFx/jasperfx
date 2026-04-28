using System.Diagnostics;
using JasperFx.CodeGeneration.Commands;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.CodeGeneration;

/// <summary>
/// Extension methods for <see cref="ICodeFile"/> that orchestrate code generation
/// without binding to any specific <see cref="IAssemblyGenerator"/> implementation.
/// <para>
/// These methods replace the equivalent methods in
/// <c>JasperFx.RuntimeCompiler.CodeFileExtensions</c>, which had a hidden
/// <c>?? new AssemblyGenerator()</c> fallback that hard-bound consumers to the
/// <c>JasperFx.RuntimeCompiler</c> package (and through it, Roslyn). Consumers
/// targeting AOT or that want to remove Roslyn from their production deployments
/// can now reference only this method (in the <see cref="JasperFx.CodeGeneration"/>
/// namespace), provide a registered <see cref="IAssemblyGenerator"/> if they need
/// runtime compilation, and otherwise omit the <c>JasperFx.RuntimeCompiler</c>
/// package reference entirely.
/// </para>
/// </summary>
public static class CodeFileExtensions
{
    /// <summary>
    /// Initialize dynamic code compilation by either loading the expected type
    /// from the supplied assembly or dynamically generating and compiling the code
    /// on demand.
    /// <para>
    /// Unlike the legacy
    /// <c>JasperFx.RuntimeCompiler.CodeFileExtensions.InitializeSynchronously</c>,
    /// this method does NOT silently fall back to creating an internal Roslyn
    /// <c>AssemblyGenerator</c>. If the configured <see cref="GenerationRules.TypeLoadMode"/>
    /// requires runtime compilation but no <see cref="IAssemblyGenerator"/> is
    /// registered in the supplied <paramref name="services"/>, an
    /// <see cref="InvalidOperationException"/> is thrown with guidance to either
    /// pre-generate code (Static mode) or register an <see cref="IAssemblyGenerator"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ExpectedTypeMissingException">
    /// Thrown when <see cref="GenerationRules.TypeLoadMode"/> is
    /// <see cref="TypeLoadMode.Static"/> and the expected pre-built type is not
    /// found in the application assembly.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when runtime compilation is required (Dynamic mode, or Auto-mode
    /// fallback) but no <see cref="IAssemblyGenerator"/> is registered in DI.
    /// </exception>
    public static void InitializeSynchronously(this ICodeFile file, GenerationRules rules,
        ICodeFileCollection parent, IServiceProvider? services)
    {
        var logger = services?.GetService(typeof(ILogger<IAssemblyGenerator>)) as ILogger ?? NullLogger.Instance;
        var @namespace = parent.ToNamespace(rules);

        if (rules.TypeLoadMode == TypeLoadMode.Dynamic)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Generated code for {Namespace}.{FileName}", parent.ChildNamespace, file.FileName);
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

            return;
        }

        var found = file.AttachTypesSynchronously(rules, rules.ApplicationAssembly, services, @namespace);
        if (found && logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Types from code file {Namespace}.{FileName} were loaded from assembly {Assembly}",
                parent.ChildNamespace, file.FileName, rules.ApplicationAssembly.GetName());
        }

        if (!found)
        {
            if (rules.TypeLoadMode == TypeLoadMode.Static && !DynamicCodeBuilder.WithinCodegenCommand)
            {
                throw new ExpectedTypeMissingException(
                    $"Could not load expected pre-built types for code file {file.FileName} ({file}) from assembly {rules.ApplicationAssembly.FullName}. You may want to verify that this is the correct assembly for pre-generated types.");
            }

            var generatedAssembly = parent.StartAssembly(rules);
            file.AssembleTypes(generatedAssembly);
            var serviceVariables = services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource;
            if (serviceVariables != null && file.TryReplaceServiceProvider(out var serviceProvider))
            {
                serviceVariables.ReplaceServiceProvider(serviceProvider);
            }

            var compiler = ResolveAssemblyGenerator(services);
            compiler.Compile(generatedAssembly, serviceVariables, out var code);

            if (serviceVariables != null && serviceVariables.ServiceLocations().Any())
            {
                file.AssertServiceLocationsAreAllowed(serviceVariables.ServiceLocations(), services);
            }

            file.AttachTypesSynchronously(rules, generatedAssembly.Assembly!, services, @namespace);

            if (rules.SourceCodeWritingEnabled)
            {
                file.WriteCodeFile(parent, rules, code!);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Generated and compiled code in memory for {Namespace}.{FileName} ({File})",
                    parent.ChildNamespace, file.FileName, file);
            }
        }
    }

    /// <summary>
    /// Async variant of <see cref="InitializeSynchronously"/>. Same semantics
    /// regarding <see cref="IAssemblyGenerator"/> resolution.
    /// </summary>
    /// <exception cref="ExpectedTypeMissingException"/>
    /// <exception cref="InvalidOperationException"/>
    public static async Task Initialize(this ICodeFile file, GenerationRules rules,
        ICodeFileCollection parent, IServiceProvider? services)
    {
        var @namespace = parent.ToNamespace(rules);

        if (rules.TypeLoadMode == TypeLoadMode.Dynamic)
        {
            Console.WriteLine($"Generated code for {parent.ChildNamespace}.{file.FileName}");

            var generatedAssembly = parent.StartAssembly(rules);
            file.AssembleTypes(generatedAssembly);
            var serviceVariables = parent is ICodeFileCollectionWithServices
                ? services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource
                : null;

            var compiler = ResolveAssemblyGenerator(services);
            compiler.Compile(generatedAssembly, serviceVariables);
            await file.AttachTypes(rules, generatedAssembly.Assembly!, services, @namespace);

            return;
        }

        var found = await file.AttachTypes(rules, rules.ApplicationAssembly, services, @namespace);
        if (found)
        {
            Console.WriteLine($"Types from code file {parent.ChildNamespace}.{file.FileName} were loaded from assembly {rules.ApplicationAssembly.GetName()}");
        }

        if (!found)
        {
            if (rules.TypeLoadMode == TypeLoadMode.Static && !DynamicCodeBuilder.WithinCodegenCommand)
            {
                throw new ExpectedTypeMissingException(
                    $"Could not load expected pre-built types for code file {file.FileName} ({file})");
            }

            var generatedAssembly = parent.StartAssembly(rules);
            file.AssembleTypes(generatedAssembly);
            var serviceVariables = services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource;

            var compiler = ResolveAssemblyGenerator(services);
            compiler.Compile(generatedAssembly, serviceVariables, out var code);

            await file.AttachTypes(rules, generatedAssembly.Assembly!, services, @namespace);

            if (rules.SourceCodeWritingEnabled)
            {
                file.WriteCodeFile(parent, rules, code);
            }

            Console.WriteLine($"Generated and compiled code in memory for {parent.ChildNamespace}.{file.FileName}");
        }
    }

    /// <summary>
    /// Write the supplied generated code to the configured export directory.
    /// </summary>
    public static void WriteCodeFile(this ICodeFile file, ICodeFileCollection parent, GenerationRules rules, string code)
    {
        try
        {
            var directory = parent.ToExportDirectory(rules.GeneratedCodeOutputPath);
            var fileName = Path.Combine(directory, file.FileName.Replace(" ", "_") + ".cs");
            File.WriteAllText(fileName, code);
            Console.WriteLine("Generated code to " + fileName.ToFullPath());
        }
        catch (Exception e)
        {
            Console.WriteLine("Unable to write code file for " + file.FileName);
            Console.WriteLine(e.ToString());
        }
    }

    /// <summary>
    /// Resolve the <see cref="IAssemblyGenerator"/> from the supplied service
    /// provider, throwing a clear, actionable error if none is registered.
    /// </summary>
    /// <remarks>
    /// The legacy <c>JasperFx.RuntimeCompiler.CodeFileExtensions</c> silently
    /// fell back to <c>new AssemblyGenerator()</c>, which masked the missing
    /// registration and dragged Roslyn into every consumer's deployment. Callers
    /// that need runtime compilation must now register an
    /// <see cref="IAssemblyGenerator"/> in DI (typically via
    /// <c>services.AddSingleton&lt;IAssemblyGenerator, AssemblyGenerator&gt;()</c>
    /// from the <c>JasperFx.RuntimeCompiler</c> package). Callers that pre-generate
    /// all code in Static mode never reach this method and so do not need the
    /// dependency at all.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IAssemblyGenerator"/> registered.</exception>
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
