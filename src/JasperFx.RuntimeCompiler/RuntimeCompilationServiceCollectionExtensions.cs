using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.RuntimeCompiler;

/// <summary>
/// Service-collection extensions for opting into runtime Roslyn compilation.
///
/// Call <see cref="AddRuntimeCompilation"/> on the application's
/// <see cref="IServiceCollection"/> when the app uses
/// <see cref="DynamicTypeLoader"/> or <see cref="AutoTypeLoader"/> and therefore
/// needs an <see cref="IAssemblyGenerator"/>. Apps that pre-generate all code
/// in <see cref="TypeLoadMode.Static"/> mode should NOT call this — leaving it
/// out lets the trimmer/AOT toolchain drop the entire <c>JasperFx.RuntimeCompiler</c>
/// dependency.
/// </summary>
public static class RuntimeCompilationServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="AssemblyGenerator"/> as the singleton
    /// <see cref="IAssemblyGenerator"/> for runtime Roslyn compilation.
    /// Idempotent — safe to call from multiple Critter Stack extensions
    /// (Marten, Wolverine) without producing duplicate registrations.
    /// </summary>
    [RequiresDynamicCode("Registers AssemblyGenerator, which compiles C# at runtime via Roslyn.")]
    [RequiresUnreferencedCode("Registers AssemblyGenerator, which emits and loads runtime-generated types that the trimmer cannot statically see.")]
    public static IServiceCollection AddRuntimeCompilation(this IServiceCollection services)
    {
        services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();
        return services;
    }
}
