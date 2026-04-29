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
            // Inside `codegen write`/`codegen preview` etc. we expect to be
            // generating the missing types — let the calling command drive
            // generation through its own pipeline rather than throwing.
            return;
        }

        throw new ExpectedTypeMissingException(
            $"Could not load expected pre-built types for code file {file.FileName} ({file}) from assembly {rules.ApplicationAssembly.FullName}. " +
            $"You may want to verify that this is the correct assembly for pre-generated types.");
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
