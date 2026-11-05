using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JasperFx.Events.Documents;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// A guard against a store leaving a compliance suite out silently (jasperfx#1091). A store calls
/// <see cref="AssertEnrolled(Assembly, object, IReadOnlyDictionary{Type, string})" /> from one test in
/// its compliance assembly. The compliance sources compile into that assembly, so the abstract suites
/// and the store's concrete subclasses are both found by reflection over it, by type, never by name.
/// </summary>
public static class ComplianceSuiteInventory
{
    private static readonly string NewLine = global::System.Environment.NewLine;

    /// <summary>
    /// Fails unless every leaf abstract suite for the fixture's family (document suites for a
    /// <see cref="DocumentStorageComplianceFixture" />, event suites for an
    /// <see cref="EventStoreComplianceFixture{TOperations, TQuerySession}" />) is either enrolled by a
    /// concrete subclass closed over the fixture's type, or excused with a reason.
    /// </summary>
    /// <param name="assembly">The store's compliance assembly.</param>
    /// <param name="fixture">An instance of the store's fixture; read for its capability flags.</param>
    /// <param name="excused">Open generic suite type (<c>typeof(X&lt;,,&gt;)</c>) to the reason it is left out.</param>
    public static void AssertEnrolled(Assembly assembly, object fixture,
        IReadOnlyDictionary<Type, string>? excused = null)
    {
        var problems = Check(SafeTypes(assembly), fixture, excused);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Compliance suite enrollment problems for {fixture.GetType().Name}:{NewLine}"
                + string.Join(NewLine, problems.Select(x => " - " + x)));
        }
    }

    /// <summary>
    /// The checking itself, over an explicit set of types so it can be exercised without an assembly.
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<Type> types, object fixture,
        IReadOnlyDictionary<Type, string>? excused = null)
    {
        excused ??= new Dictionary<Type, string>();
        var problems = new List<string>();
        var fixtureType = fixture.GetType();
        var suiteRoot = SuiteRootFor(fixtureType);
        if (suiteRoot == null)
        {
            return new[]
            {
                $"{fixtureType.Name} is neither a DocumentStorageComplianceFixture nor an EventStoreComplianceFixture"
            };
        }

        var all = types.ToList();
        var suites = all.Where(t => t.IsAbstract && !t.IsInterface && t != suiteRoot && DerivesFrom(t, suiteRoot))
            .ToList();

        // A leaf is an abstract suite no other abstract suite derives from. Intermediate bases such as
        // NumericRevisionComplianceBase are covered by the leaves under them.
        var leaves = suites.Where(s => !suites.Any(o => o != s && DerivesFrom(o, Definition(s)))).ToList();
        var concrete = all.Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition).ToList();

        foreach (var leaf in leaves)
        {
            var enrolled = concrete.Any(c => Enrolls(c, leaf, fixtureType));
            var reason = excused.TryGetValue(Definition(leaf), out var r) ? r : null;
            var flag = leaf.GetCustomAttribute<RequiresCapabilityAttribute>(false)?.FlagName;
            var name = Definition(leaf).Name;

            if (reason != null)
            {
                if (enrolled)
                {
                    problems.Add($"{name} is excused ('{reason}') but is enrolled anyway; remove the stale excuse");
                }

                if (flag != null && ReadFlag(fixture, flag, problems, name))
                {
                    problems.Add(
                        $"{name} is excused ('{reason}') but {fixtureType.Name}.{flag} is true; the store says it supports this, so the suite cannot be left out");
                }
            }
            else if (!enrolled)
            {
                problems.Add(
                    $"{name} has no concrete subclass over {fixtureType.Name} in the assembly and is not excused"
                    + (flag != null ? $" (gated by {flag})" : ""));
            }
        }

        foreach (var key in excused.Keys)
        {
            if (!leaves.Any(l => Definition(l) == key))
            {
                problems.Add($"{key.Name} is excused but is not a compliance suite known to this assembly");
            }
        }

        return problems;
    }

    private static bool ReadFlag(object fixture, string flag, List<string> problems, string suite)
    {
        var property = fixture.GetType().GetProperty(flag, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || property.PropertyType != typeof(bool))
        {
            problems.Add($"{suite} requires capability '{flag}', which {fixture.GetType().Name} has no public bool property for");
            return false;
        }

        return (bool)property.GetValue(fixture)!;
    }

    private static Type? SuiteRootFor(Type fixtureType)
    {
        if (typeof(DocumentStorageComplianceFixture).IsAssignableFrom(fixtureType))
        {
            return typeof(DocumentStorageComplianceSuite<>);
        }

        for (var t = fixtureType; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("EventStoreComplianceFixture"))
            {
                return typeof(EventStoreComplianceSuite<,,>);
            }
        }

        return null;
    }

    private static Type Definition(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition() : t;

    private static bool DerivesFrom(Type type, Type ancestorDefinition)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            if (Definition(t) == ancestorDefinition)
            {
                return true;
            }
        }

        return false;
    }

    // The concrete class enrolls the leaf when its base chain reaches the leaf's definition with the
    // fixture type as the first generic argument (TFixture is first on every suite).
    private static bool Enrolls(Type concrete, Type leaf, Type fixtureType)
    {
        var definition = Definition(leaf);
        for (var t = concrete.BaseType; t != null; t = t.BaseType)
        {
            if (Definition(t) != definition)
            {
                continue;
            }

            return !t.IsGenericType || t.GetGenericArguments()[0] == fixtureType;
        }

        return false;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null)!;
        }
    }
}
