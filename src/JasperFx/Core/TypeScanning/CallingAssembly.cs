using System.Diagnostics;
using System.Reflection;

namespace JasperFx.Core.TypeScanning;

/// <summary>
///     Use to walk up the execution stack and "find" the assembly
///     that originates the call. Ignores system assemblies, test runner
///     assemblies, and any assembly marked with the [IgnoreAssembly] attribute
/// </summary>
public class CallingAssembly
{
    private static readonly string[] _prefixesToIgnore = { "System.", "Microsoft." };

    public static Assembly? Find()
    {
        // GH-600: this used to render the stack as TEXT (Environment.StackTrace) and then guess each
        // frame's assembly by trying to Assembly.Load progressively shorter dotted prefixes of the method
        // name. That could only ever resolve an assembly whose name lined up with its namespace, so it
        // missed some frames outright and adopted others it should have skipped -- NUnit's NUnit.Framework
        // namespace matches its nunit.framework assembly exactly, for instance, so a scan configured from
        // an async test adopted the runner. Reading the assembly off the frame is exact, needs no
        // speculative loads, and drops a static List<string> cache that was being mutated from every
        // thread that ever called in here.
        var frames = new StackTrace().GetFrames();

        foreach (var frame in frames)
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;

            if (assembly is null)
            {
                continue;
            }

            if (isSystemAssembly(assembly))
            {
                continue;
            }

            return assembly;
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

        return assemblyName != null && isSystemAssembly(assemblyName);
    }

    private static bool isSystemAssembly(string assemblyName)
    {
        // GH-600: the frames between JasperFx and the code that configured the scan belong to the test
        // runner under an async fixture, and adopting one means scanning an assembly that holds none of
        // the application's types. Shares JasperFxOptions' list so the two stack walks agree on what a
        // runner is.
        return _prefixesToIgnore.Any(x => assemblyName.StartsWith(x, StringComparison.Ordinal))
               || JasperFxOptions.IsTestRunnerAssembly(assemblyName);
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
