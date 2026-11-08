using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace JasperFx.Core.TypeScanning;

internal static class BaselineAssemblyContext
{
#if NET461
		public static readonly IBaselineAssemblyLoadContext Loader = new CustomAssemblyLoadContext();
#else
    public static readonly IJasperFxAssemblyLoadContext Loader =
        new AssemblyLoadContextWrapper(AssemblyLoadContext.Default);
#endif
}

/// <summary>
///     Utility to discover and load assemblies installed in your application for extensibility or plugin schems
/// </summary>
public static class AssemblyFinder
{
    /// <summary>
    ///     Find assemblies in the application's binary path
    /// </summary>
    /// <param name="logFailure">Take an action when an assembly file could not be loaded</param>
    /// <param name="filter">Allow list filter of assemblies to scan</param>
    /// <param name="includeExeFiles">Optionally include *.exe files</param>
    /// <returns></returns>
    [RequiresUnreferencedCode("FindAssemblies scans the application base directory for assemblies and inspects their referenced-assembly graph. The trimmer cannot statically reason about which types those assemblies contribute; AOT-publishing apps should rely on explicit assembly references and source-generated type lists instead.")]
    public static IEnumerable<Assembly> FindAssemblies(Action<string> logFailure, Func<Assembly, bool> filter,
        bool includeExeFiles)
    {
        string path;
        try
        {
            path = AppContext.BaseDirectory;
        }
        catch (Exception)
        {
            path = Directory.GetCurrentDirectory();
        }

        return FindAssemblies(filter, path, logFailure, includeExeFiles);
    }

    /// <summary>
    ///     Find assemblies in the given path
    /// </summary>
    /// <param name="assemblyPath">The path to probe for assembly files</param>
    /// <param name="logFailure">Take an action when an assembly file could not be loaded</param>
    /// <param name="includeExeFiles">Optionally include *.exe files</param>
    /// <returns></returns>
    [RequiresUnreferencedCode("FindAssemblies scans the given path for assemblies and inspects their referenced-assembly graph. The trimmer cannot statically reason about which types those assemblies contribute; AOT-publishing apps should rely on explicit assembly references and source-generated type lists instead.")]
    public static IEnumerable<Assembly> FindAssemblies(Func<Assembly, bool> filter, string assemblyPath,
        Action<string> logFailure, bool includeExeFiles)
    {
        var assemblies = findAssemblies(assemblyPath, logFailure, includeExeFiles)
            .Where(filter)
            .OrderBy(x => x.GetName().Name)
            .ToArray();

        Assembly[] FindDependencies(Assembly a)
        {
            return assemblies.Where(x => a.GetReferencedAssemblies().Any(_ => _.Name == x.GetName().Name)).ToArray();
        }

        return assemblies.TopologicalSort((Func<Assembly, Assembly[]>)FindDependencies, false);
    }

    // wolverine#4232: every public entry point into this walk already carries the attribute; the private
    // iterator did not, so the trimmer reported the LoadFromAssemblyPath call from inside the generated
    // MoveNext -- a warning a consumer could neither act on nor suppress. Annotating it puts the diagnostic
    // where the decision actually is, on the public FindAssemblies overloads.
    [RequiresUnreferencedCode("Scans a directory and loads each assembly it finds by name or path. The trimmer cannot statically reason about which types those assemblies contribute; AOT-publishing apps should rely on explicit assembly references and source-generated type lists instead.")]
    private static IEnumerable<Assembly> findAssemblies(string assemblyPath, Action<string> logFailure,
        bool includeExeFiles)
    {
        var dllFiles = Directory.EnumerateFiles(assemblyPath, "*.dll", SearchOption.AllDirectories);
        var files = dllFiles;

        if (includeExeFiles)
        {
            var exeFiles = Directory.EnumerateFiles(assemblyPath, "*.exe", SearchOption.AllDirectories);
            files = dllFiles.Concat(exeFiles);
        }

        foreach (var file in files)
        {
            if (ShouldSkipFile(Path.GetRelativePath(assemblyPath, file)))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            Assembly? assembly = null;

            try
            {
                assembly = BaselineAssemblyContext.Loader.LoadFromAssemblyName(new AssemblyName(name));
            }
            catch (Exception)
            {
                try
                {
                    assembly = BaselineAssemblyContext.Loader.LoadFromAssemblyPath(file);
                }
                catch (Exception)
                {
                    logFailure(file);
                }
            }

            if (assembly != null)
            {
                yield return assembly;
            }
        }
    }


    private static readonly char[] _separators = ['/', '\\'];

    /// <summary>
    ///     Whether the scan should pass over a file without trying to load it, given its path
    ///     relative to the folder being scanned. That is a file under a <c>runtimes</c> or
    ///     <c>native</c> folder, which hold RID-specific and native binaries (such as the code
    ///     coverage collector's <c>covrun64.dll</c>) that the default load context either resolves
    ///     through deps.json or cannot load at all, or a satellite resource assembly in a culture
    ///     folder (<c>de/App.resources.dll</c>), which holds no types and fails to load by name.
    ///     Trying any of them only produces failed loads.
    /// </summary>
    /// <remarks>
    ///     jasperfx#1143: this used to test the full path for "/runtimes" or "/native", which never
    ///     matched on Windows, where the paths use '\\', and on Linux skipped everything when the
    ///     application itself lived under a folder whose name began with "native". Only the folder
    ///     segments below the scanned folder are compared now, splitting on both separators.
    ///     A satellite is recognized by the lower-case ".resources.dll" suffix the build gives it and
    ///     by sitting in a subfolder, so an ordinary top-level assembly named "Company.Resources" is
    ///     still scanned.
    /// </remarks>
    internal static bool ShouldSkipFile(string relativePath)
    {
        var segments = relativePath.Split(_separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        // The last segment is the file name itself.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("native", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return segments[^1].EndsWith(".resources.dll", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Find assembly files matching a given filter
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="includeExeFiles"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode("FindAssemblies scans the application base directory and inspects the referenced-assembly graph. AOT-publishing apps should rely on explicit assembly references and source-generated type lists.")]
    public static IEnumerable<Assembly> FindAssemblies(Func<Assembly, bool>? filter,
        bool includeExeFiles = false)
    {
        filter ??= _ => true;

        return FindAssemblies(_ => { }, filter, includeExeFiles);
    }
}

internal interface IJasperFxAssemblyLoadContext
{
    [RequiresUnreferencedCode("Loads an assembly the trimmer hasn't seen statically.")]
    Assembly LoadFromStream(Stream assembly);

    Assembly LoadFromAssemblyName(AssemblyName assemblyName);

    [RequiresUnreferencedCode("Loads an assembly the trimmer hasn't seen statically.")]
    Assembly LoadFromAssemblyPath(string assemblyName);
}

internal sealed class AssemblyLoadContextWrapper : IJasperFxAssemblyLoadContext
{
    private readonly AssemblyLoadContext _ctx;

    public AssemblyLoadContextWrapper(AssemblyLoadContext ctx)
    {
        _ctx = ctx;
    }

    [RequiresUnreferencedCode("Wraps AssemblyLoadContext.LoadFromStream, which loads an assembly the trimmer hasn't seen statically.")]
    public Assembly LoadFromStream(Stream assembly)
    {
        return _ctx.LoadFromStream(assembly);
    }

    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        return _ctx.LoadFromAssemblyName(assemblyName);
    }

    [RequiresUnreferencedCode("Wraps AssemblyLoadContext.LoadFromAssemblyPath, which loads an assembly the trimmer hasn't seen statically.")]
    public Assembly LoadFromAssemblyPath(string assemblyName)
    {
        return _ctx.LoadFromAssemblyPath(assemblyName);
    }
}

internal static class TopologicalSortExtensions
{
    /// <summary>
    ///     Find the index within the enumerable of the first item that matches the condition
    /// </summary>
    /// <param name="enumerable"></param>
    /// <param name="condition"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    internal static int GetFirstIndex<T>(this IEnumerable<T> enumerable, Func<T, bool> condition)
    {
        var index = -1;
        foreach (var item in enumerable)
        {
            index++;
            if (condition(item))
            {
                return index;
            }
        }

        return -1;
    }

    internal static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source,
        Func<T, IEnumerable<T>> getDependencies, bool throwOnCycle = true)
    {
        return source.TopologicalSort(x => getDependencies(x).ToList().GetEnumerator(), throwOnCycle);
    }

    internal static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source,
        Func<T, IEnumerator<T>> getDependencies, bool throwOnCycle = true)
    {
        var sorted = new List<T>();
        var visited = new HashSet<T>();

        // These don't strictly need to be kept outside of the loop, but it saves us from having to reallocate them in every Visit call
        var visiting = new HashSet<T>();
        var stack = new Stack<(T, IEnumerator<T>)>();

        foreach (var item in source) Visit(item, visited, visiting, sorted, stack, getDependencies, throwOnCycle);

        return sorted;
    }

    private static void Visit<T>(T root, ISet<T> visited, ISet<T> visiting, ICollection<T> sorted,
        Stack<(T, IEnumerator<T>)> stack, Func<T, IEnumerator<T>> getDependencies, bool throwOnCycle)
    {
        if (!visited.Add(root))
        {
            return;
        }

        stack.Push((root, getDependencies(root)));
        visiting.Add(root);

        while (stack.Count > 0)
        {
            var (parent, enumerator) = stack.Peek();
            if (!enumerator.MoveNext())
            {
                visiting.Remove(parent);
                sorted.Add(parent);
                stack.Pop();
                continue;
            }

            var child = enumerator.Current;
            if (!visited.Add(child))
            {
                if (throwOnCycle && visiting.Contains(child))
                {
                    throw new Exception("Cyclic dependency found");
                }

                continue;
            }

            visiting.Add(child);
            stack.Push((child, getDependencies(child)));
        }

        visiting.Remove(root);
    }
}