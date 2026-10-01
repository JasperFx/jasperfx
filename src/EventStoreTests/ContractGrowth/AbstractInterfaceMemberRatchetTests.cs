using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.Documents;
using JasperFx.Events;
using Shouldly;

namespace EventStoreTests.ContractGrowth;

/// <summary>
/// A new abstract member on an existing public interface is a runtime break for every package that
/// implements it and was compiled against an older JasperFx (jasperfx#931, jasperfx#933).
/// </summary>
/// <remarks>
/// <para>
/// Marten, Polecat, Fisher and Wolverine implement JasperFx interfaces and all declare an open-ended
/// JasperFx range, so NuGet routinely resolves a store built against an older minor beside a newer
/// JasperFx — through any other package that lifts it. Restore and build are clean; the application
/// then dies at startup with a <see cref="TypeLoadException" />, in a method its own <c>try</c> cannot
/// guard. That is what 2.77 did to every released Marten.
/// </para>
/// <para>
/// The policy: <b>in a minor release, a member added to an existing interface carries a default
/// implementation</b> — one that answers what it can without the implementer, or throws
/// <see cref="NotSupportedException" /> naming the implementing type — and new abstract members wait
/// for a major. A brand-new interface may be all-abstract, because nothing implements it yet.
/// </para>
/// <para>
/// <c>abstract-interface-members.txt</c> is the record of every abstract member as of the last
/// release. The first test is the rule; the second keeps the record honest as interfaces are added
/// or (in a major) members are removed. Regenerate the record with
/// <c>JASPERFX_UPDATE_CONTRACT_BASELINE=true</c> set while running this class — after reading the
/// diff, never to silence the first test.
/// </para>
/// </remarks>
public class AbstractInterfaceMemberRatchetTests
{
    private const string BaselineFile = "abstract-interface-members.txt";

    private static readonly Assembly[] Contracts =
    [
        typeof(IDocumentStoreDiagnostics).Assembly, // JasperFx
        typeof(IEventStore).Assembly // JasperFx.Events
    ];

    [Fact]
    public void no_existing_interface_gains_an_abstract_member()
    {
        var baseline = readBaseline();
        var knownInterfaces = baseline.Select(interfaceOf).ToHashSet();

        var added = currentAbstractMembers()
            .Where(x => knownInterfaces.Contains(interfaceOf(x)) && !baseline.Contains(x))
            .ToList();

        added.ShouldBeEmpty(
            "These abstract members were added to interfaces that already shipped. A package compiled against " +
            "the previous JasperFx that implements one of them will throw TypeLoadException at startup the " +
            "moment this release is resolved beside it, with no warning at restore or build. Give each a default " +
            "implementation (answer what can be answered without the implementer, else throw " +
            "NotSupportedException naming GetType()), or move it to a new interface. See jasperfx#933.");
    }

    [Fact]
    public void the_baseline_matches_the_current_contracts()
    {
        if (Environment.GetEnvironmentVariable("JASPERFX_UPDATE_CONTRACT_BASELINE") == "true")
        {
            File.WriteAllLines(sourceBaselinePath(), currentAbstractMembers());
            return;
        }

        var baseline = readBaseline();
        var current = currentAbstractMembers();

        current.Except(baseline).ShouldBeEmpty(
            $"New interfaces (or new members on them) are missing from {BaselineFile}. Rerun with " +
            "JASPERFX_UPDATE_CONTRACT_BASELINE=true and commit the file.");

        baseline.Except(current).ShouldBeEmpty(
            $"{BaselineFile} lists members that are no longer abstract or no longer exist. Removing an " +
            "abstract member from a shipped interface is a breaking change for its callers; if that is " +
            "intended, rerun with JASPERFX_UPDATE_CONTRACT_BASELINE=true and commit the file.");
    }

    private static List<string> currentAbstractMembers()
    {
        return Contracts
            .SelectMany(exportedTypes)
            .Where(t => t.IsInterface)
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.DeclaredOnly)
                .Where(m => m.IsAbstract)
                .Select(m => $"{t.FullName} :: {m}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<Type> exportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>().Where(t => t.IsVisible);
        }
    }

    private static string interfaceOf(string member) => member[..member.IndexOf(" :: ", StringComparison.Ordinal)];

    /// <summary>Read from the copy in the output directory, so a CI path map cannot hide it.</summary>
    private static HashSet<string> readBaseline()
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ContractGrowth", BaselineFile))
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string sourceBaselinePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, BaselineFile);
}
