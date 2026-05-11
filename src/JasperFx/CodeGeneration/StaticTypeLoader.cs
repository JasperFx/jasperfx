using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.CodeGeneration;

/// <summary>
/// AOT- and trim-safe <see cref="ITypeLoader"/> that only reads pre-generated
/// types out of <see cref="GenerationRules.ApplicationAssembly"/>. Throws when
/// the expected type cannot be found and we are not currently in a codegen
/// command (i.e. <c>dotnet run -- codegen write</c>), where missing types are
/// expected because the user is generating them.
/// </summary>
public sealed class StaticTypeLoader : ITypeLoader
{
    public void Initialize(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        var logger = services?.GetService(typeof(ILogger<ITypeLoader>)) as ILogger ?? NullLogger.Instance;
        var @namespace = parent.ToNamespace(rules);

        var found = file.AttachTypesSynchronously(rules, rules.ApplicationAssembly, services, @namespace);
        if (found)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Types from code file {Namespace}.{FileName} were loaded from assembly {Assembly}",
                    parent.ChildNamespace, file.FileName, rules.ApplicationAssembly.GetName());
            }
            return;
        }

        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            // Inside `codegen write`/`codegen preview` etc. some callers
            // (notably ProviderGraph.CreateDocumentProvider<T> in Marten,
            // building a compiled-query plan as part of the codegen-write
            // pipeline) need an actually-attached type to be returned —
            // not just "we'll generate it later". Pre-build was missing,
            // so compile in-memory now via the Roslyn pipeline. Marten#4354
            // tracks the bug this addresses.
            //
            // The AOT contract isn't violated: WithinCodegenCommand is set
            // only by JasperFx's command-line codegen targets (a tool-time
            // operation). Production runtime always sees it as false, so
            // this branch is unreachable in a PublishAot=true deployment.
            CompileAndAttachAtCodegenTime(file, rules, parent, services);
            return;
        }

        throw new ExpectedTypeMissingException(
            $"Could not load expected pre-built types for code file {file.FileName} ({file}) from assembly {rules.ApplicationAssembly.FullName}. " +
            $"You may want to verify that this is the correct assembly for pre-generated types.");
    }

    /// <summary>
    /// Codegen-tool-time fallback when a pre-built type is missing.
    /// Suppressed from the AOT analyzer because this path is gated on
    /// <see cref="DynamicCodeBuilder.WithinCodegenCommand"/>, which is
    /// only ever true during <c>codegen write</c> / <c>codegen preview</c>
    /// tool invocations — never in a PublishAot=true production deployment.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL2026",
        Justification = "Reachable only during tool-time codegen-command execution, never at production runtime.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reachable only during tool-time codegen-command execution, never at production runtime.")]
    private static void CompileAndAttachAtCodegenTime(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        DynamicTypeLoader.CompileAndAttach(file, rules, parent, services, out _);
    }

    public Task InitializeAsync(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        Initialize(file, rules, parent, services);
        return Task.CompletedTask;
    }
}
