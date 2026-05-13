using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

#region sample_IRegistrationConvention

/// <summary>
///     Used to create custom type scanning conventions.
/// </summary>
/// <remarks>
///     Convention-based registration walks types discovered from loaded assemblies
///     and constructs <see cref="ServiceDescriptor"/>s from them. The trimmer cannot
///     reason statically about which types survive, and any <c>MakeGenericType</c>
///     used by an open-generic convention needs runtime code generation. AOT-publishing
///     apps should either avoid convention-based registration entirely (use explicit
///     <c>services.AddSingleton&lt;TService, TImpl&gt;()</c> calls) or substitute a
///     source-generated registration manifest.
/// </remarks>
public interface IRegistrationConvention
{
    [RequiresUnreferencedCode("Scans TypeSet for types matching the convention and constructs ServiceDescriptors reflectively. Discovered types and their constructors must survive trimming.")]
    [RequiresDynamicCode("Open-generic conventions close types via MakeGenericType, which requires runtime code generation.")]
    void ScanTypes(TypeSet types, IServiceCollection services);
}

#endregion