using JasperFx.Core.Reflection;
﻿using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Model;

public enum ServiceLocationPolicy
{
    /// <summary>
    /// The code generation will allow code generation using service location, but will emit a console warning
    /// </summary>
    AllowedButWarn,
    
    /// <summary>
    /// The code generation will allow code generation using service location and suppresses all warnings
    /// </summary>
    AlwaysAllowed,
    
    /// <summary>
    /// The code generation will reject all code generation that requires service locations
    /// </summary>
    NotAllowed
}

public record ServiceLocationReport(ServiceDescriptor ServiceDescriptor, string Reason);

public interface IServiceVariableSource : IVariableSource
{
    void ReplaceVariables(IMethodVariables method);

    void StartNewType();
    void StartNewMethod();

    bool TryFindKeyedService(Type type, string key, out Variable? variable);

    /// <summary>
    /// Find the keyed registration of <paramref name="type" /> that <paramref name="key" /> selects, with the
    /// key as declared on <c>[FromKeyedServices]</c> rather than as a string, so an enum or any other
    /// non-string key finds its registration (jasperfx#1189).
    /// </summary>
    /// <remarks>
    /// A default member, so an implementation compiled against an earlier JasperFx keeps loading (jasperfx#933).
    /// The default answers string keys through <see cref="TryFindKeyedService(Type, string, out Variable?)" />
    /// and refuses any other key rather than guess at a match.
    /// </remarks>
    bool TryFindKeyedService(Type type, object key, out Variable? variable)
    {
        if (key is string stringKey)
        {
            return TryFindKeyedService(type, stringKey, out variable);
        }

        throw new NotSupportedException(
            $"{GetType().FullNameInCode()} does not support non-string service keys, so it cannot resolve " +
            $"[FromKeyedServices({key.GetType().FullNameInCode()} {key})] for {type.FullNameInCode()}. " +
            $"Override {nameof(IServiceVariableSource)}.{nameof(TryFindKeyedService)}(Type, object, out Variable?).");
    }

    void ReplaceServiceProvider(Variable serviceProvider)
    {
        // Nothing, but implement for realsies!
    }

    // GH-2991: undo a prior per-file ReplaceServiceProvider so a shared source instance (the CLI
    // codegen path reuses one across all files) can isolate the override per ICodeFile.
    void ResetServiceProvider()
    {
        // Nothing by default.
    }

    ServiceLocationReport[] ServiceLocations() => [];


}

