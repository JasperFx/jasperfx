﻿using System.Globalization;
using System.Reflection;
using JasperFx.Core.TypeScanning;

namespace JasperFx.Core.TypeScanning;

/// <summary>
///     Use to walk up the execution stack and "find" the assembly
///     that originates the call. Ignores system assemblies and any
///     assembly marked with the [IgnoreOnScanning] attribute
/// </summary>
public class CallingAssembly
{
    private static readonly string[] _prefixesToIgnore = { "System.", "Microsoft." };

    private static readonly IList<string> _misses = new List<string>();

    /// <summary>
    ///     Method is used to get the stack trace in english
    /// </summary>
    /// <returns>Stack trace in english</returns>
    private static string GetStackTraceInEnglish()
    {
        var currentUiCulture = Thread.CurrentThread.CurrentUICulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        var trace = System.Environment.StackTrace;
        Thread.CurrentThread.CurrentUICulture = currentUiCulture;
        return trace;
    }


    public static Assembly? Find()
    {
        var trace = GetStackTraceInEnglish();


        var parts = trace.Split('\n');

        for (var i = 0; i < parts.Length; i++)
        {
            var line = parts[i];
            var assembly = findAssembly(line);
            if (assembly != null && !isSystemAssembly(assembly))
            {
                return assembly;
            }
        }

        return Assembly.GetEntryAssembly();
    }

    private static bool isSystemAssembly(Assembly? assembly)
    {
        if (assembly == null)
        {
            return false;
        }

        if (assembly.GetCustomAttributes<IgnoreAssemblyAttribute>().Any())
        {
            return true;
        }

        var assemblyName = assembly.GetName().Name;

        return isSystemAssembly(assemblyName!);
    }

    private static bool isSystemAssembly(string assemblyName)
    {
        return _prefixesToIgnore.Any(x => assemblyName.StartsWith(x));
    }

    private static Assembly? findAssembly(string stacktraceLine)
    {
        var candidate = stacktraceLine.Trim().Substring(3);

        // Short circuit this
        if (isSystemAssembly(candidate))
        {
            return null;
        }

        Assembly? assembly = null;
        var names = candidate.Split('.');
        for (var i = names.Length - 2; i > 0; i--)
        {
            var possibility = string.Join(".", names.Take(i).ToArray());

            if (_misses.Contains(possibility))
            {
                continue;
            }

            try
            {
                assembly = Assembly.Load(new AssemblyName(possibility));
                break;
            }
            catch
            {
                _misses.Add(possibility);
            }
        }

        return assembly;
    }

    /// <summary>
    ///     Finds the calling assembly from the specified type
    /// </summary>
    /// <param name="registry"></param>
    /// <returns></returns>
    public static Assembly? DetermineApplicationAssembly(object registry)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        var assembly = registry.GetType().Assembly;
        return isSystemAssembly(assembly) ? Find() : assembly;
    }
}