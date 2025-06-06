﻿using System.Collections;
using System.Diagnostics;

namespace JasperFx.Core;

/// <summary>
///     Taken directly from Marten:
///     https://github.com/JasperFx/marten/blob/2f18d09fa2034cbc647f48a74bbf3bbb8ea51116/src/Marten/Util/EnumerableExtensions.cs
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>
    ///     Find the index within the enumerable of the first item that matches the condition
    /// </summary>
    /// <param name="enumerable"></param>
    /// <param name="condition"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static int GetFirstIndex<T>(this IEnumerable<T> enumerable, Func<T, bool> condition)
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

    /// <summary>
    /// Find the last index within the enumerable that matches the condition
    /// </summary>
    /// <param name="enumerable"></param>
    /// <param name="condition"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static int GetLastIndex<T>(this IReadOnlyList<T> enumerable, Func<T, bool> condition)
    {
        for (int i = enumerable.Count -1; i >= 0; i--)
        {
            if (condition(enumerable[i])) return i;
        }

        return -1;
    }

    public static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source,
        Func<T, IEnumerable<T>> getDependencies, bool throwOnCycle = true)
    {
        return source.TopologicalSort(x => getDependencies(x).ToList().GetEnumerator(), throwOnCycle);
    }

    public static IEnumerable<T> TopologicalSort<T>(this IEnumerable<T> source, Func<T, IEnumerator<T>> getDependencies,
        bool throwOnCycle = true)
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

        // These should be empty by the end of the function.
        Debug.Assert(visiting.Count == 0);
        Debug.Assert(stack.Count == 0);
    }


    /// <summary>
    ///     Adds the value to the list if it does not already exist
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="value"></param>
    public static void Fill<T>(this IList<T> list, T value)
    {
        if (list.Contains(value))
        {
            return;
        }

        list.Add(value);
    }

    /// <summary>
    ///     Adds a series of values to a list if they do not already exist in the list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="values"></param>
    public static void Fill<T>(this IList<T> list, IEnumerable<T> values)
    {
        list.AddRange(values.Where(v => !list.Contains(v)));
    }

    /// <summary>
    ///     Removes all of the items that match the provided condition
    /// </summary>
    /// <typeparam name="T">The type of the items in the list</typeparam>
    /// <param name="list">The list to modify</param>
    /// <param name="whereEvaluator">The test to determine if an item should be removed</param>
    public static void RemoveAll<T>(this IList<T> list, Func<T, bool> whereEvaluator)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (whereEvaluator(list[i]))
            {
                list.RemoveAt(i);
            }
        }
    }

    /// <summary>
    ///     Performs an action with a counter for each item in a sequence and provides
    /// </summary>
    /// <typeparam name="T">The type of the items in the sequence</typeparam>
    /// <param name="values">The sequence to iterate</param>
    /// <param name="eachAction">The action to performa on each item</param>
    /// <returns></returns>
    public static IEnumerable<T> Each<T>(this IEnumerable<T> values, Action<T, int> eachAction)
    {
        var index = 0;
        foreach (var item in values) eachAction(item, index++);

        return values;
    }

    [DebuggerStepThrough]
    public static IEnumerable<T> Each<T>(this IEnumerable<T> values, Action<T> eachAction)
    {
        foreach (var item in values) eachAction(item);

        return values;
    }

    [DebuggerStepThrough]
    public static IEnumerable Each(this IEnumerable values, Action<object> eachAction)
    {
        foreach (var item in values) eachAction(item);

        return values;
    }

    /// <summary>
    ///     Returns the first non-null value from executing the func against the enumerable
    /// </summary>
    /// <typeparam name="TItem"></typeparam>
    /// <typeparam name="TReturn"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="func"></param>
    /// <returns></returns>
    public static TReturn? FirstValue<TItem, TReturn>(this IEnumerable<TItem> enumerable, Func<TItem, TReturn?> func)
        where TReturn : class
    {
        return enumerable.Select(func).FirstOrDefault(@object => @object != null);
    }

    /// <summary>
    ///     Add many items to a list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="items"></param>
    /// <returns></returns>
    public static IList<T> AddMany<T>(this IList<T> list, params T[] items)
    {
        return list.AddRange(items);
    }

    /// <summary>
    ///     Appends a sequence of items to an existing list
    /// </summary>
    /// <typeparam name="T">The type of the items in the list</typeparam>
    /// <param name="list">The list to modify</param>
    /// <param name="items">The sequence of items to add to the list</param>
    /// <returns></returns>
    public static IList<T> AddRange<T>(this IList<T> list, IEnumerable<T> items)
    {
        foreach (var item in items) list.Add(item);
        return list;
    }
}