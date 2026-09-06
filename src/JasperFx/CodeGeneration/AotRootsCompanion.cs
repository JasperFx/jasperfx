using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace JasperFx.CodeGeneration;

/// <summary>
///     Emits the "AOT roots companion" — a generated class whose only content is a
///     <c>[ModuleInitializer]</c>-marked static method carrying a block of
///     <c>[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(...))]</c> attributes.
/// </summary>
/// <remarks>
///     <para>
///         The problem this solves (jasperfx#743, follow-on from wolverine#4287): pre-generated
///         code is reached at runtime by reflection — an assembly scan plus
///         <c>Activator.CreateInstance</c> — so ILC has no static reference to it, trims it, and
///         <c>TypeLoadMode.Static</c> silently degrades to a scan that finds nothing under Native
///         AOT. Until now the application author had to hand-write the rooting attributes.
///     </para>
///     <para>
///         A module initializer is an unconditional ILC root, so anchoring the block there roots
///         the whole graph with zero application-side code. Note that
///         <c>[DynamicDependency]</c> has <c>AttributeUsage(Constructor | Field | Method)</c> and
///         therefore cannot sit on the class — the method is the only legal home, which is why
///         the companion is a class *and* a method rather than an attribute block on the registry.
///     </para>
/// </remarks>
public static class AotRootsCompanionExtensions
{
    /// <summary>
    ///     The name of the generated rooting method.
    /// </summary>
    public const string PinMethodName = "Pin";

    private const string BodyComment =
        "Intentionally empty. The [DynamicDependency] attributes above are the payload: they root the generated types for Native AOT, and [ModuleInitializer] guarantees this method is itself an ILC root.";

    /// <summary>
    ///     Add an AOT rooting companion to this generated assembly.
    /// </summary>
    /// <param name="assembly">The assembly being emitted.</param>
    /// <param name="typeName">Name of the companion class, e.g. <c>"AotRoots"</c>.</param>
    /// <param name="rootedTypes">
    ///     One argument per type to root. Use <see cref="AttributeArg.TypeNamed" /> for sibling
    ///     generated types — they have no runtime <see cref="Type" /> while <c>codegen write</c>
    ///     is running — and <see cref="AttributeArg.Type" /> for types that already exist.
    /// </param>
    /// <returns>
    ///     The generated companion type, or <c>null</c> when nothing was emitted: either the
    ///     assembly is being rendered as F# (see the remarks) or <paramref name="rootedTypes" />
    ///     was empty.
    /// </returns>
    /// <remarks>
    ///     The companion is <b>C# only</b>. The F# compiler does not honor
    ///     <c>ModuleInitializerAttribute</c> — an F# module's <c>do</c> bindings initialize lazily
    ///     and are not an ILC root — so emitting one under
    ///     <c>codegen write --language fsharp</c> would be a silent no-op that reads as working
    ///     rooting. This method returns <c>null</c> instead.
    /// </remarks>
    public static GeneratedType? AddAotRoots(this GeneratedAssembly assembly, string typeName,
        IEnumerable<AttributeArg> rootedTypes)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        if (assembly.TargetLanguage != CodegenLanguage.csharp)
        {
            return null;
        }

        var roots = rootedTypes?.ToArray() ?? [];
        if (roots.Length == 0)
        {
            return null;
        }

        var companion = assembly.AddType(typeName);
        companion.CommentType(
            "Native AOT rooting companion (jasperfx#743). Generated code is only ever reached reflectively, so without these roots ILC trims it and TypeLoadMode.Static finds nothing.");

        var method = companion.AddStaticVoidMethod(PinMethodName);
        method.Frames.Add(new Frames.NoOpFrame(BodyComment));

        method.Attributes.Add(new GeneratedAttribute(typeof(ModuleInitializerAttribute)));

        foreach (var root in roots)
        {
            method.Attributes.Add(new GeneratedAttribute(typeof(DynamicDependencyAttribute),
                AttributeArg.Enum(DynamicallyAccessedMemberTypes.All), root));
        }

        return companion;
    }

    /// <summary>
    ///     Add an AOT rooting companion for types named in code. Use this overload for sibling
    ///     generated types, which do not exist as runtime <see cref="Type" />s at
    ///     <c>codegen write</c> time. Names must be fully qualified.
    /// </summary>
    public static GeneratedType? AddAotRoots(this GeneratedAssembly assembly, string typeName,
        IEnumerable<string> rootedTypeNames)
    {
        return assembly.AddAotRoots(typeName,
            (rootedTypeNames ?? []).Select(AttributeArg.TypeNamed));
    }

    /// <summary>
    ///     Add an AOT rooting companion for types that already exist at code generation time —
    ///     handler classes, message types, closed generics, and so on.
    /// </summary>
    public static GeneratedType? AddAotRoots(this GeneratedAssembly assembly, string typeName,
        IEnumerable<Type> rootedTypes)
    {
        return assembly.AddAotRoots(typeName, (rootedTypes ?? []).Select(AttributeArg.Type));
    }
}
