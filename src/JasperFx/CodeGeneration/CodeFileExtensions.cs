using JasperFx.Core;

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
        rules.Loader.Initialize(file, rules, parent, services);
    }

    /// <summary>
    /// Async variant of <see cref="InitializeSynchronously"/>. Same semantics
    /// regarding <see cref="IAssemblyGenerator"/> resolution.
    /// </summary>
    /// <exception cref="ExpectedTypeMissingException"/>
    /// <exception cref="InvalidOperationException"/>
    public static Task Initialize(this ICodeFile file, GenerationRules rules,
        ICodeFileCollection parent, IServiceProvider? services)
    {
        return rules.Loader.InitializeAsync(file, rules, parent, services);
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

}
