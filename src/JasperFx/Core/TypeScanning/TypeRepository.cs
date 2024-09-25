using System.Reflection;

namespace JasperFx.Core.TypeScanning;

/// <summary>
///     Mechanism to analyze and scan assemblies for exported types
/// </summary>
public static class TypeRepository
{
    private static ImHashMap<Assembly, AssemblyTypes>
        _assemblies = ImHashMap<Assembly, AssemblyTypes>.Empty;

    public static void ClearAll()
    {
        _assemblies = ImHashMap<Assembly, AssemblyTypes>.Empty;
    }

    /// <summary>
    ///     Use to assert that there were no failures in type scanning when trying to find the exported types
    ///     from any Assembly
    /// </summary>
    public static void AssertNoTypeScanningFailures()
    {
        var exceptions =
            FailedAssemblies().Select(x => x.Record.LoadException).Where(x => x != null).ToArray();


        if (exceptions.Any())
        {
            throw new AggregateException(exceptions!);
        }
    }

    /// <summary>
    ///     Query for all assemblies that could not be scanned, usually because
    ///     of missing dependencies
    /// </summary>
    /// <returns></returns>
    public static IReadOnlyList<AssemblyTypes> FailedAssemblies()
    {
        return _assemblies
            .Enumerate()
            .Select(x => x.Value)
            .Where(x => x.Record.LoadException != null)
            .ToArray();
    }

    /// <summary>
    ///     Scan a single assembly
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public static AssemblyTypes ForAssembly(Assembly assembly)
    {
        if (_assemblies.TryFind(assembly, out var types))
        {
            return types;
        }

        types = new AssemblyTypes(assembly);
        _assemblies = _assemblies.AddOrUpdate(assembly, types);

        return types;
    }

    /// <summary>
    ///     Find types matching a certain criteria from an assembly
    /// </summary>
    /// <param name="assemblies"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static TypeSet FindTypes(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter = null)
    {
        return new TypeSet(assemblies.Select(ForAssembly), filter);
    }


    /// <summary>
    ///     Find types matching a certain criteria and TypeClassification from an Assembly
    /// </summary>
    /// <param name="assemblies"></param>
    /// <param name="classification"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static IEnumerable<Type> FindTypes(IEnumerable<Assembly> assemblies,
        TypeClassification classification, Func<Type, bool>? filter = null)
    {
        var query = new TypeQuery(classification);
        query.Includes.WithCondition("User defined", filter);
        return assemblies.Select(ForAssembly).SelectMany(query.Find);
    }


    public static IEnumerable<Type> FindTypes(Assembly? assembly, TypeClassification classification,
        Func<Type, bool>? filter = null)
    {
        if (assembly == null)
        {
            return Array.Empty<Type>();
        }

        filter ??= _ => true;
        
        var query = new TypeQuery(classification, filter);
        return query.Find(ForAssembly(assembly));
    }
}