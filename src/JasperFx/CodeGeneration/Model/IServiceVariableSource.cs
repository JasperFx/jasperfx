using Microsoft.Extensions.DependencyInjection;

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

