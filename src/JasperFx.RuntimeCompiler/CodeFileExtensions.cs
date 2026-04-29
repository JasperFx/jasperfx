using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx.RuntimeCompiler
{
    /// <summary>
    /// Diagnostic helpers that exercise the Roslyn pipeline directly.
    ///
    /// The legacy <c>InitializeSynchronously</c> / <c>Initialize</c> /
    /// <c>WriteCodeFile</c> extensions formerly defined here have been removed in
    /// 2.0 — use the equivalents in <see cref="JasperFx.CodeGeneration.CodeFileExtensions"/>
    /// (the <see cref="JasperFx.CodeGeneration"/> namespace), which dispatch to
    /// <see cref="ITypeLoader"/> and let consumers omit <c>JasperFx.RuntimeCompiler</c>
    /// entirely when only the static / pre-generated path is required.
    ///
    /// Migration:
    /// <list type="bullet">
    ///   <item>
    ///     <c>using JasperFx.RuntimeCompiler;</c> → <c>using JasperFx.CodeGeneration;</c>
    ///     for <c>InitializeSynchronously</c> / <c>Initialize</c> / <c>WriteCodeFile</c>.
    ///   </item>
    ///   <item>
    ///     Register an <see cref="IAssemblyGenerator"/> in DI when runtime compilation is
    ///     required, e.g. <c>services.AddRuntimeCompilation()</c> from this package, or
    ///     <c>services.AddSingleton&lt;IAssemblyGenerator, AssemblyGenerator&gt;()</c>.
    ///   </item>
    /// </list>
    /// </summary>
    public static class RuntimeCompilerHostExtensions
    {
        /// <summary>
        ///     Validate the configuration of the supplied host by attempting to compile
        ///     every <see cref="ICodeFile"/> registered through <see cref="ICodeFileCollection"/>.
        ///     Useful as a smoke test in CI for applications that pre-generate code.
        /// </summary>
        /// <exception cref="AggregateException">
        ///     One or more code files failed to compile. Failures are listed in
        ///     <see cref="AggregateException.InnerExceptions"/>.
        /// </exception>
        [RequiresDynamicCode("AssertAllGeneratedCodeCanCompile uses Roslyn at runtime to compile each ICodeFile.")]
        [RequiresUnreferencedCode("AssertAllGeneratedCodeCanCompile uses Roslyn at runtime; compiled types may be inaccessible to the trimmer.")]
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
