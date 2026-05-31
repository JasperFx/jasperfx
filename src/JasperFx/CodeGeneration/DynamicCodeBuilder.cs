using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Util;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class DynamicCodeBuilder
{
    static DynamicCodeBuilder()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (args.Any(x => x.EqualsIgnoreCase("codegen")))
        {
            WithinCodegenCommand = true;
        }
    }
    
    public DynamicCodeBuilder(IServiceProvider services, ICodeFileCollection[] collections)
    {
        Services = services;
        Collections = collections;
    }

    public IServiceVariableSource? ServiceVariableSource { get; set; }

    public string[] ChildNamespaces => Collections.Select(x => x.ChildNamespace).ToArray();

    public IServiceProvider Services { get; }

    public ICodeFileCollection[] Collections { get; }

    public string GenerateAllCode()
    {
        WithinCodegenCommand = true;

        try
        {
            var writer = new StringWriter();

            foreach (var generator in Collections)
            {
                var code = generateCode(generator);
                writer.WriteLine(code);
                writer.WriteLine();
                writer.WriteLine();
            }

            return writer.ToString();
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public void DeleteAllGeneratedCode()
    {
        WithinCodegenCommand = true;

        try
        {
            foreach (var directory in Collections.Select(x => x.Rules.GeneratedCodeOutputPath).Distinct())
            {
                FileSystem.CleanDirectory(directory);
                FileSystem.DeleteDirectoryIfExists(directory);

                Console.WriteLine($"Deleted directory {directory}");
            }
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public string GenerateCodeFor(string childNamespace)
    {
        WithinCodegenCommand = true;

        try
        {
            var generator = Collections.FirstOrDefault(x => x.ChildNamespace.EqualsIgnoreCase(childNamespace));
            if (generator == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"Unknown {nameof(childNamespace)} '{childNamespace}'. Known code types are {String.Join(", ", ChildNamespaces)}");
            }

            return generateCode(generator);
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public void WriteGeneratedCode(Action<string> onFileWritten)
    {
        WithinCodegenCommand = true;

        try
        {
            foreach (var collection in Collections)
            {
                var directory = collection.Rules.GeneratedCodeOutputPath.ToFullPath();
                FileSystem.CreateDirectoryIfNotExists(directory);

                var exportDirectory = collection.ToExportDirectory(directory);

                var withServices = collection is ICodeFileCollectionWithServices;
                var services = withServices ? ServiceVariableSource : null;

                foreach (var file in collection.BuildFiles())
                {
                    var generatedAssembly = collection.StartAssembly(collection.Rules);
                    file.AssembleTypes(generatedAssembly);

                    // #2991: apply any per-file service-provider override (e.g. HTTP's
                    // httpContext.RequestServices), matching the runtime DynamicTypeLoader path.
                    // `services` is shared across every file here, so reset the prior override first.
                    applyServiceProviderOverride(file, services);

                    var code = generatedAssembly.GenerateCode(services);

                    // #227: enforce ServiceLocationPolicy per-file, matching the
                    // runtime compile path in DynamicTypeLoader.Initialize. The
                    // CLI codegen paths (preview / write / test) historically
                    // bypassed this hook, so a host setting
                    // ServiceLocationPolicy.NotAllowed got the expected
                    // InvalidServiceLocationException at runtime but a clean
                    // "all generated" result from the CLI.
                    assertServiceLocationsAllowed(file, services);

                    var fileName = Path.Combine(exportDirectory, file.FileName.Replace(" ", "_") + ".cs");
                    File.WriteAllText(fileName, code);
                    onFileWritten(fileName);
                }
            }
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    private string generateCode(ICodeFileCollection collection)
    {
        if (collection.ChildNamespace.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Missing {nameof(ICodeFileCollection.ChildNamespace)} for {collection}");
        }

        // #227: per-file generation + per-file ServiceLocationPolicy enforcement,
        // matching the runtime compile path's per-ICodeFile granularity.
        // ServiceCollectionServerVariableSource resets _serviceLocations on every
        // StartNewMethod / StartNewType call, so the only point where we can
        // observe a file's reports is right after its own GenerateCode finishes —
        // which means we have to iterate files independently rather than batching
        // them all into a single GeneratedAssembly the way this method used to.
        var withServices = collection is ICodeFileCollectionWithServices;
        var services = withServices ? ServiceVariableSource : null;

        var writer = new StringWriter();
        foreach (var file in collection.BuildFiles())
        {
            var generatedAssembly = collection.StartAssembly(collection.Rules);
            try
            {
                file.AssembleTypes(generatedAssembly);
            }
            catch (Exception e)
            {
                throw new CodeGenerationException(file, e);
            }

            // #2991: see WriteGeneratedCode — honor a per-file service-provider override on the
            // shared source.
            applyServiceProviderOverride(file, services);

            var code = generatedAssembly.GenerateCode(services);
            assertServiceLocationsAllowed(file, services);

            writer.WriteLine(code);
        }

        return writer.ToString();
    }

    /// <summary>
    ///     Attempts to generate all the known code types in the system
    /// </summary>
    /// <param name="withAssembly"></param>
    /// <exception cref="GeneratorCompilationFailureException"></exception>
    public void TryBuildAndCompileAll(Action<GeneratedAssembly, IServiceVariableSource?> withAssembly)
    {
        foreach (var collection in Collections)
        {
            // #227: iterate per ICodeFile so we can enforce ServiceLocationPolicy
            // at file granularity (matching DynamicTypeLoader.Initialize). The
            // previous batched-per-collection compile silently bypassed the
            // policy hook because ServiceLocations() resets on every
            // StartNewMethod, leaving only the last-method-of-last-file's
            // reports visible at the end of a batched compile.
            var withServices = collection is ICodeFileCollectionWithServices;
            var services = withServices ? ServiceVariableSource : null;

            foreach (var file in collection.BuildFiles())
            {
                var generatedAssembly = collection.StartAssembly(collection.Rules);
                file.AssembleTypes(generatedAssembly);

                // #2991: see WriteGeneratedCode.
                applyServiceProviderOverride(file, services);

                try
                {
                    withAssembly(generatedAssembly, services);
                }
                catch (Exception e)
                {
                    throw new GeneratorCompilationFailureException(collection, e);
                }

                assertServiceLocationsAllowed(file, services);
            }
        }
    }

    // GH-2991: honor a per-file ServiceProviderSource override (e.g. HTTP's httpContext.RequestServices)
    // in the CLI codegen paths, matching DynamicTypeLoader.Initialize/CompileAndAttach. Because the CLI
    // reuses a single shared IServiceVariableSource across every file, reset any prior override first so
    // it cannot leak into a following file that should keep the default isolated-and-scoped provider.
    private static void applyServiceProviderOverride(ICodeFile file, IServiceVariableSource? services)
    {
        if (services == null) return;

        services.ResetServiceProvider();
        if (file.TryReplaceServiceProvider(out var serviceProvider))
        {
            services.ReplaceServiceProvider(serviceProvider);
        }
    }

    private void assertServiceLocationsAllowed(ICodeFile file, IServiceVariableSource? services)
    {
        if (services == null) return;

        var reports = services.ServiceLocations();
        if (reports.Length == 0) return;

        file.AssertServiceLocationsAreAllowed(reports, Services);
    }

    /// <summary>
    ///     Attach pre-built types in the application assembly
    /// </summary>
    /// <param name="assembly">
    ///     The assembly containing the pre-built types. If null, this falls back to the entry assembly of
    ///     the running application
    /// </param>
    public async Task LoadPrebuiltTypes(Assembly? assembly = null)
    {
        foreach (var collection in Collections)
        {
            foreach (var file in collection.BuildFiles())
            {
                var @namespace = $"{collection.Rules.GeneratedNamespace}.{collection.ChildNamespace}";
                await file.AttachTypes(collection.Rules, assembly ?? collection.Rules.ApplicationAssembly, Services,
                    @namespace);
            }
        }
    }

    public static bool WithinCodegenCommand { get; set; } = false;
}