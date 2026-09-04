using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public enum TypeLoadMode
{
    /// <summary>
    ///     Always generate new types at runtime. This is appropriate for
    ///     development time when configuration may be in flux
    /// </summary>
    Dynamic,

    /// <summary>
    ///     Try to load generated types from the target application assembly,
    ///     but generate types if there is no pre-built types and export
    ///     the new source code
    /// </summary>
    Auto,

    /// <summary>
    ///     Types must be loaded from the pre-built application assembly, or
    ///     the loading will throw exceptions
    /// </summary>
    Static
}

public class GenerationRules
{
    public readonly IList<Assembly> Assemblies = new List<Assembly>();

    public readonly IDictionary<string, object> Properties = new Dictionary<string, object>();

    public readonly IList<IVariableSource> Sources = new List<IVariableSource>();
    private bool _sourceCodeWritingEnabled = true;
    private TypeLoadMode _typeLoadMode = TypeLoadMode.Dynamic;
    private ITypeLoader? _loader;

    public GenerationRules(string applicationNamespace) : this()
    {
        GeneratedNamespace = applicationNamespace;
    }

    public GenerationRules(string applicationNamespace, TypeLoadMode typeLoadMode) : this(applicationNamespace)
    {
        TypeLoadMode = typeLoadMode;
    }

    public GenerationRules()
    {
    }

    /// <summary>
    /// Use a scoped IServiceProvider to resolve the service T in all cases even
    /// if other services can be built through constructors. Work around for Wolverine
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public GenerationRules AlwaysUseServiceLocationFor<T>()
    {
        return AlwaysUseServiceLocationFor(typeof(T));
    }
    
    /// <summary>
    /// Use a scoped IServiceProvider to resolve the service T in all cases even
    /// if other services can be built through constructors. Work around for Wolverine
    /// </summary>
    /// <param name="serviceType"></param>
    /// <returns></returns>
    public GenerationRules AlwaysUseServiceLocationFor(Type serviceType)
    {
        var source = new LazyServiceLocationVariableSource(serviceType);
        Sources.Add(source);
        return this;
    }
    
    
    
    public List<IMethodPreCompilationPolicy> MethodPreCompilation { get; } = new();

    public bool SourceCodeWritingEnabled
    {
        get => _sourceCodeWritingEnabled;
        set
        {
            // Doesn't matter the value
            SourceCodeWritingEnabledHasChanged = true;
            _sourceCodeWritingEnabled = value;
        }
    }
    
    public bool SourceCodeWritingEnabledHasChanged { get; private set; }


    public string GeneratedNamespace { get; set; } = "Internal.Generated";

    public TypeLoadMode TypeLoadMode
    {
        get => _typeLoadMode;
        set
        {
            // Doesn't matter the value
            TypeLoadModeHasChanged = true;
            _typeLoadMode = value;

            // Resync the loader so changes to TypeLoadMode are reflected on the
            // next access. A consumer that explicitly assigned Loader takes
            // precedence over this and is preserved.
            if (!_loaderExplicitlySet)
            {
                _loader = null;
            }
        }
    }

    public bool TypeLoadModeHasChanged { get; private set; }

    private bool _loaderExplicitlySet;

    /// <summary>
    /// The strategy used to resolve runtime types backing each
    /// <see cref="ICodeFile"/>. Defaults to a value derived from
    /// <see cref="TypeLoadMode"/> — <see cref="StaticTypeLoader"/> for
    /// <see cref="TypeLoadMode.Static"/>, <see cref="DynamicTypeLoader"/> for
    /// <see cref="TypeLoadMode.Dynamic"/>, <see cref="AutoTypeLoader"/> for
    /// <see cref="TypeLoadMode.Auto"/>.
    ///
    /// Consumers can replace this to plug in custom loading (e.g. an
    /// AOT-friendly source-generated loader) without having to introduce a new
    /// <see cref="TypeLoadMode"/>.
    /// </summary>
    public ITypeLoader Loader
    {
        get => _loader ??= DefaultLoaderFor(_typeLoadMode);
        set
        {
            _loader = value ?? throw new ArgumentNullException(nameof(value));
            _loaderExplicitlySet = true;
        }
    }

    // wolverine#4232: the two dynamic loaders are RequiresUnreferencedCode + RequiresDynamicCode, and the
    // #pragma that used to sit here only silenced the analyzer while JasperFx itself compiled. Trim and AOT
    // analysis runs over IL in the CONSUMER's publish, where a pragma means nothing, so every app publishing
    // AOT saw both warnings out of a method it does not call.
    //
    // The guard is not a suppression dressed up as a branch -- it is also simply true: neither loader can
    // emit a type into a Native AOT image. Auto exists to adapt to the platform, so it adapts; Dynamic was
    // asked for something the platform cannot do, and saying so here beats the obscure failure it used to
    // reach later. The suppressions sit on the split overload below, where the constructions are.
    private static ITypeLoader DefaultLoaderFor(TypeLoadMode mode)
        => DefaultLoaderFor(mode, RuntimeFeature.IsDynamicCodeSupported);

    // Split for testability, the same way JasperFxOptions.CannotBeApplicationAssembly is: the
    // dynamic-code-unsupported branch cannot be reached from a CoreCLR test process.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The dynamic loaders are only constructed on the dynamicCodeSupported branch, which a Native AOT image never takes; RuntimeFeature.IsDynamicCodeSupported is the only production caller's argument.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The dynamic loaders are only constructed on the dynamicCodeSupported branch, which a Native AOT image never takes; RuntimeFeature.IsDynamicCodeSupported is the only production caller's argument.")]
    internal static ITypeLoader DefaultLoaderFor(TypeLoadMode mode, bool dynamicCodeSupported)
    {
        if (!dynamicCodeSupported)
        {
            if (mode == TypeLoadMode.Dynamic)
            {
                throw new PlatformNotSupportedException(
                    $"{nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Dynamic)} generates and loads types at runtime, which a Native AOT image cannot do. " +
                    $"Use {nameof(TypeLoadMode)}.{nameof(TypeLoadMode.Static)} with types generated ahead of time by the 'codegen write' command, " +
                    $"or assign {nameof(GenerationRules)}.{nameof(Loader)} your own {nameof(ITypeLoader)}.");
            }

            return new StaticTypeLoader();
        }

        return mode switch
        {
            TypeLoadMode.Static => new StaticTypeLoader(),
            TypeLoadMode.Dynamic => new DynamicTypeLoader(),
            TypeLoadMode.Auto => new AutoTypeLoader(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    public string GeneratedCodeOutputPath { get; set; } = "Internal/Generated";

    public Assembly ApplicationAssembly { get; set; } = Assembly.GetEntryAssembly()!;

    /// <summary>
    ///     Reference the given assembly in the compilation
    /// </summary>
    /// <param name="assembly"></param>
    public void ReferenceAssembly(Assembly assembly)
    {
        Assemblies.Fill(assembly);
    }

    /// <summary>
    ///     Recursively reference assemblies from the supplied types, including generic
    ///     argument types
    /// </summary>
    /// <param name="types"></param>
    public void ReferenceTypes(params Type[] types)
    {
        foreach (var assembly in WalkReferencedAssemblies.ForTypes(types).Distinct()) Assemblies.Fill(assembly);
    }

    /// <summary>
    ///     Returns a shallow clone of this <see cref="GenerationRules"/> with
    ///     copied collections so mutating helpers like
    ///     <see cref="ReferenceTypes"/>, <see cref="ReferenceAssembly"/>, or
    ///     <see cref="AlwaysUseServiceLocationFor{T}"/> on the clone don't
    ///     affect the source. Intended for callers that want to cache an
    ///     expensive-to-build base configuration once and add per-call-site
    ///     refinements without locking. <see cref="TypeLoadMode"/>,
    ///     <see cref="SourceCodeWritingEnabled"/>, and a consumer-supplied
    ///     <see cref="Loader"/> are only re-applied on the clone when their
    ///     change-tracking flags are set, so the clone starts out with the
    ///     same has-changed view as the source.
    /// </summary>
    public GenerationRules Clone()
    {
        var clone = new GenerationRules
        {
            GeneratedNamespace = GeneratedNamespace,
            GeneratedCodeOutputPath = GeneratedCodeOutputPath,
            ApplicationAssembly = ApplicationAssembly,
        };

        if (TypeLoadModeHasChanged)
        {
            clone.TypeLoadMode = TypeLoadMode;
        }

        if (SourceCodeWritingEnabledHasChanged)
        {
            clone.SourceCodeWritingEnabled = SourceCodeWritingEnabled;
        }

        if (_loaderExplicitlySet)
        {
            clone.Loader = _loader!;
        }

        foreach (var assembly in Assemblies)
        {
            clone.Assemblies.Add(assembly);
        }

        foreach (var pair in Properties)
        {
            clone.Properties[pair.Key] = pair.Value;
        }

        foreach (var source in Sources)
        {
            clone.Sources.Add(source);
        }

        foreach (var policy in MethodPreCompilation)
        {
            clone.MethodPreCompilation.Add(policy);
        }

        return clone;
    }
}

/// <summary>
///     Find unique assemblies from the supplied types, including types
///     from generic arguments
/// </summary>
public static class WalkReferencedAssemblies
{
    public static IEnumerable<Assembly> ForTypes(params Type[] types)
    {
        var stack = new Stack<Type>();

        foreach (var type in types)
        {
            stack.Push(type);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null) yield break;
                
                yield return current.Assembly;

                if (!current.IsGenericType || current.IsGenericTypeDefinition)
                {
                    continue;
                }

                var typeArguments = current.GetGenericArguments();
                foreach (var typeArgument in typeArguments) stack.Push(typeArgument);
            }
        }
    }
}