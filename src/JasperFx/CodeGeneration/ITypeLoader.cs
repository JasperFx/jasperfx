using JasperFx.Core;

namespace JasperFx.CodeGeneration;

/// <summary>
/// Strategy for resolving the runtime <see cref="Type"/> backing a generated
/// <see cref="ICodeFile"/>.
///
/// Historically every consumer (Marten, Wolverine) branched on
/// <see cref="GenerationRules.TypeLoadMode"/> directly: in <see cref="TypeLoadMode.Static"/>
/// they read pre-generated types out of the entry assembly; in
/// <see cref="TypeLoadMode.Dynamic"/> they routed through
/// <c>JasperFx.RuntimeCompiler</c> and Roslyn. This interface gives that branch
/// a single recognizable seam: callers ask the loader to initialize a code file,
/// and AOT/trim toolchains can drop the dynamic implementation when only the
/// static path is registered.
///
/// Default implementations:
/// <list type="bullet">
///   <item><see cref="StaticTypeLoader"/> — reads pre-generated types only; never compiles. AOT-safe.</item>
///   <item><see cref="DynamicTypeLoader"/> — always compiles in-memory via <see cref="IAssemblyGenerator"/>. Requires runtime code generation.</item>
///   <item><see cref="AutoTypeLoader"/> — tries static first, falls back to dynamic on miss. Requires runtime code generation.</item>
/// </list>
/// </summary>
public interface ITypeLoader
{
    /// <summary>
    /// Synchronously initialize the supplied <paramref name="file"/>: resolve
    /// (or generate, depending on the implementation) the runtime types and
    /// attach them via <see cref="ICodeFile.AttachTypesSynchronously"/>.
    /// </summary>
    void Initialize(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services);

    /// <summary>
    /// Asynchronous variant of <see cref="Initialize"/>.
    /// </summary>
    Task InitializeAsync(
        ICodeFile file,
        GenerationRules rules,
        ICodeFileCollection parent,
        IServiceProvider? services);
}
