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


    /// <summary>
    ///     Find assembly files matching a given filter
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="includeExeFiles"></param>
    /// <returns></returns>
    public static IEnumerable<Assembly> FindAssemblies(Func<Assembly, bool>? filter,
        bool includeExeFiles = false)
    {
        filter ??= _ => true;

        return FindAssemblies(_ => { }, filter, includeExeFiles);
    }
}

internal interface IJasperFxAssemblyLoadContext
{
    Assembly LoadFromStream(Stream assembly);
    Assembly LoadFromAssemblyName(AssemblyName assemblyName);
    Assembly LoadFromAssemblyPath(string assemblyName);
}



internal sealed class AssemblyLoadContextWrapper : IJasperFxAssemblyLoadContext
{
    private readonly AssemblyLoadContext _ctx;

    public AssemblyLoadContextWrapper(AssemblyLoadContext ctx)
    {
        _ctx = ctx;
    }

    public Assembly LoadFromStream(Stream assembly)
    {
        return _ctx.LoadFromStream(assembly);
    }

    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        return _ctx.LoadFromAssemblyName(assemblyName);
    }

    public Assembly LoadFromAssemblyPath(string assemblyName)
    {
        return _ctx.LoadFromAssemblyPath(assemblyName);
    }
}


internal static class TopologicalSortExtensions
{
        /// <summary>
        /// Find the index within the enumerable of the first item that matches the condition
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
                if (condition(item)) return index;
            }

            return -1;
        }

        internal static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source,
            Func<T, IEnumerable<T>> getDependencies, bool throwOnCycle = true)
        {
            return source.TopologicalSort(x => getDependencies(x).ToList().GetEnumerator(), throwOnCycle);
        }
        
        internal static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source, Func<T, IEnumerator<T>> getDependencies, bool throwOnCycle = true)
        {
            var sorted = new List<T>();
            var visited = new HashSet<T>();

            // These don't strictly need to be kept outside of the loop, but it saves us from having to reallocate them in every Visit call
            var visiting = new HashSet<T>();
            var stack = new Stack<(T, IEnumerator<T>)>();

            foreach (var item in source)
            {
                Visit(item, visited, visiting, sorted, stack, getDependencies, throwOnCycle);
            }

            return sorted;
        }

        private static void Visit<T>(T root, ISet<T> visited, ISet<T> visiting, ICollection<T> sorted, Stack<(T, IEnumerator<T>)> stack, Func<T, IEnumerator<T>> getDependencies, bool throwOnCycle)
        {
            if (!visited.Add(root))
                return;

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