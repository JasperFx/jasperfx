using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Commands;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.RuntimeCompiler
{
    public static class CodeFileExtensions
    {

        public static async Task Initialize(this ICodeFile file, GenerationRules rules, ICodeFileCollection parent, IServiceProvider? services)
        {
            var @namespace = parent.ToNamespace(rules);
            
            if (rules.TypeLoadMode == TypeLoadMode.Dynamic)
            {
                Console.WriteLine($"Generated code for {parent.ChildNamespace}.{file.FileName}");
                
                var generatedAssembly = parent.StartAssembly(rules);
                file.AssembleTypes(generatedAssembly);
                var serviceVariables = parent is ICodeFileCollectionWithServices ? services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource : null;

                var compiler = new AssemblyGenerator();
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


                var compiler = new AssemblyGenerator();
                compiler.Compile(generatedAssembly, serviceVariables);
                
                await file.AttachTypes(rules, generatedAssembly.Assembly!, services, @namespace);

                if (rules.SourceCodeWritingEnabled)
                {
                    var code = compiler.Code;
                    file.WriteCodeFile(parent, rules, code);
                }
                
                Console.WriteLine($"Generated and compiled code in memory for {parent.ChildNamespace}.{file.FileName}");
            }
            
        }
        
        /// <summary>
        /// Initialize dynamic code compilation by either loading the expected type
        /// from the supplied assembly or dynamically generating and compiling the code
        /// on demand
        /// </summary>
        /// <param name="file"></param>
        /// <param name="rules"></param>
        /// <param name="parent"></param>
        /// <param name="services"></param>
        /// <exception cref="ExpectedTypeMissingException"></exception>
        public static void InitializeSynchronously(this ICodeFile file, GenerationRules rules, ICodeFileCollection parent, IServiceProvider? services)
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
                var serviceVariables = parent is ICodeFileCollectionWithServices ? services?.GetService(typeof(IServiceVariableSource)) as IServiceVariableSource : null;
                if (serviceVariables != null && file.TryReplaceServiceProvider(out var serviceProvider))
                {
                    serviceVariables.ReplaceServiceProvider(serviceProvider);
                }

                var compiler = services?.GetRequiredService<IAssemblyGenerator>() ?? new AssemblyGenerator();
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
                logger.LogDebug("Types from code file {Namespace}.{FileName} were loaded from assembly {Assembly}", parent.ChildNamespace, file.FileName, rules.ApplicationAssembly.GetName());
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

                var compiler = services?.GetRequiredService<IAssemblyGenerator>() ?? new AssemblyGenerator();
                compiler.Compile(generatedAssembly, serviceVariables);
                
                if (serviceVariables != null && serviceVariables.ServiceLocations().Any())
                {
                    file.AssertServiceLocationsAreAllowed(serviceVariables.ServiceLocations(), services);
                }
                
                file.AttachTypesSynchronously(rules, generatedAssembly.Assembly!, services, @namespace);

                if (rules.SourceCodeWritingEnabled)
                {
                    var code = compiler.Code;
                    file.WriteCodeFile(parent, rules, code!);
                }
                
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Generated and compiled code in memory for {Namespace}.{FileName} ({File})", parent.ChildNamespace, file.FileName, file);
                }
            }
        }

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
        ///     Validate all of the Wolverine configuration of this Wolverine application.
        ///     This checks that all of the known generated code elements are valid
        /// </summary>
        /// <param name="host"></param>
        public static void AssertAllGeneratedCodeCanCompile(this IHost host)
        {
            var exceptions = new List<Exception>();
            var failures = new List<string>();
        
            var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var services = host.Services.GetService<IServiceVariableSource>();

            foreach (var collection in collections)
            {
                foreach (var file in collection.BuildFiles())
                {
                    var fileName = collection.ChildNamespace.Replace(".", "/").AppendPath(file.FileName);
                
                    try
                    {
                        var assembly = new GeneratedAssembly(collection.Rules);
                        file.AssembleTypes(assembly);
                        new AssemblyGenerator().Compile(assembly, services);
                    
                        Debug.WriteLine($"U+2713 {fileName} ");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Failed: {fileName}");
                        Debug.WriteLine(e);
                    
                        failures.Add(fileName);
                        exceptions.Add(e);
                    }
                }
            }

            if (failures.Any())
            {
                throw new AggregateException($"Compilation failures for:\n{failures.Join("\n")}", exceptions);
            }
        }
    }
}
