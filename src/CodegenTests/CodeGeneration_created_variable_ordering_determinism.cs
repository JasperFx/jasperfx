using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;

namespace CodegenTests;

/// <summary>
///     Companion to <see cref="CodeGeneration_ordering_determinism" />, which covers the InjectedFields
///     that become constructor parameters and fields. This covers frames that CREATE variables and are
///     pulled into the method body by MethodFrameArranger.compileFrames step 3: several independent
///     service location calls feeding two downstream calls, with nothing in the dependency graph
///     ordering the locators relative to each other.
/// </summary>
public class CodeGeneration_created_variable_ordering_determinism
{
    [Fact]
    public void generated_source_is_byte_identical_when_regenerated_in_one_process()
    {
        var first = generate();

        // Frame identity hash codes decide the order, so any single pair of generations agrees
        // about half the time. 25 comparisons is what turns "usually differs" into "differs".
        for (var i = 0; i < 25; i++) generate().ShouldBe(first);
    }

    [Fact]
    public void generated_source_does_not_depend_on_frame_hash_codes()
    {
        // Frame does not override GetHashCode, so an arbitrary identity hash per object is what
        // the ImHashMap behind DependencyGatherer.Dependencies orders by. Pin two of those values
        // and swap them: the same logical method has to emit the same source either way.
        generate(1, 2).ShouldBe(generate(2, 1));
    }

    private static string generate(int? firstHash = null, int? secondHash = null)
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Runner", typeof(IRunOrderTest));
        var method = type.MethodFor(nameof(IRunOrderTest.Run));

        var first = handlerCall(nameof(PairedRunHandler.RunFirst), firstHash);
        first.Arguments[0] = locate(nameof(LocatedServices.Alpha));
        first.Arguments[1] = locate(nameof(LocatedServices.Bravo));

        var second = handlerCall(nameof(PairedRunHandler.RunSecond), secondHash);
        second.Arguments[0] = locate(nameof(LocatedServices.Charlie));
        second.Arguments[1] = locate(nameof(LocatedServices.Delta));

        method.Frames.Add(first);
        method.Frames.Add(second);

        return assembly.GenerateCode();
    }

    private static MethodCall handlerCall(string methodName, int? hash)
    {
        return hash.HasValue
            ? new FixedHashMethodCall(typeof(PairedRunHandler), methodName, hash.Value)
            : new MethodCall(typeof(PairedRunHandler), methodName);
    }

    // A service location call the arranger has to pull into the method body, because the frame
    // that creates the variable is not in the method's own frame list.
    private static Variable locate(string methodName)
    {
        return new MethodCall(typeof(LocatedServices), methodName).ReturnVariable!;
    }

    // Identity hash codes are what the ImHashMap behind the dependency cache orders by, and they
    // are arbitrary. Pinning two of them is what makes the failure deterministic.
    private sealed class FixedHashMethodCall : MethodCall
    {
        private readonly int _hash;

        public FixedHashMethodCall(Type handlerType, string methodName, int hash) : base(handlerType, methodName)
        {
            _hash = hash;
        }

        public override int GetHashCode()
        {
            return _hash;
        }
    }
}

public static class LocatedServices
{
    public static IAlphaService Alpha() => null!;
    public static IBravoService Bravo() => null!;
    public static ICharlieService Charlie() => null!;
    public static IDeltaService Delta() => null!;
}

public static class PairedRunHandler
{
    public static void RunFirst(IAlphaService alpha, IBravoService bravo)
    {
    }

    public static void RunSecond(ICharlieService charlie, IDeltaService delta)
    {
    }
}
