using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.CodeGeneration;

/// <summary>
/// Convenience <see cref="ITypeLoader"/> for development-time / hybrid setups:
/// first try to load pre-generated types from
/// <see cref="GenerationRules.ApplicationAssembly"/>; if missing, fall back to
/// runtime compilation and (when <see cref="GenerationRules.SourceCodeWritingEnabled"/>)
/// write the freshly-compiled source out to
/// <see cref="GenerationRules.GeneratedCodeOutputPath"/> so future runs hit the
/// static path. Requires runtime code generation by definition.
/// </summary>
[RequiresDynamicCode("AutoTypeLoader falls back to DynamicTypeLoader when pre-generated types are missing.")]
[RequiresUnreferencedCode("AutoTypeLoader falls back to DynamicTypeLoader, which emits and loads runtime-generated types that the trimmer cannot statically see.")]
public sealed class AutoTypeLoader : ITypeLoader
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

        DynamicTypeLoader.CompileAndAttach(file, rules, parent, services, out var code);

        if (rules.SourceCodeWritingEnabled)
        {
            file.WriteCodeFile(parent, rules, code);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Generated and compiled code in memory for {Namespace}.{FileName} ({File})",
                parent.ChildNamespace, file.FileName, file);
        }
    }

    public async Task InitializeAsync(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services)
    {
        var @namespace = parent.ToNamespace(rules);

        var found = await file.AttachTypes(rules, rules.ApplicationAssembly, services, @namespace);
        if (found)
        {
            return;
        }

        var code = await DynamicTypeLoader.CompileAndAttachAsync(file, rules, parent, services);

        if (rules.SourceCodeWritingEnabled)
        {
            file.WriteCodeFile(parent, rules, code);
        }
    }
}
