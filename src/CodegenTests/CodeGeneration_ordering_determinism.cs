using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace CodegenTests;

public class CodeGeneration_ordering_determinism
{
    private readonly ITestOutputHelper _output;

    public CodeGeneration_ordering_determinism(ITestOutputHelper output)
    {
        _output = output;
    }

    private static GeneratedType buildTypeWithManyInjectedFields()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Runner", typeof(IRunOrderTest));
        var method = type.MethodFor(nameof(IRunOrderTest.Run));

        var call = new MethodCall(typeof(MultiArgRunHandler), nameof(MultiArgRunHandler.Run));

        // Pre-set Arguments to be InjectedFields with deliberately scrambled
        // names. The intent is to force the constructor parameter / field
        // declaration ordering through `findInjectedFields`, which today uses
        // an unordered ImHashMap and is therefore non-deterministic across
        // process runs.
        call.Arguments[0] = new InjectedField(typeof(IZebraService), "zebra");
        call.Arguments[1] = new InjectedField(typeof(IAlphaService), "alpha");
        call.Arguments[2] = new InjectedField(typeof(IMangoService), "mango");
        call.Arguments[3] = new InjectedField(typeof(IBravoService), "bravo");
        call.Arguments[4] = new InjectedField(typeof(IDeltaService), "delta");
        call.Arguments[5] = new InjectedField(typeof(ICharlieService), "charlie");

        method.Frames.Add(call);

        type.ArrangeFrames();

        return type;
    }

    [Fact]
    public void all_injected_fields_are_in_deterministic_sorted_order()
    {
        var type = buildTypeWithManyInjectedFields();

        var ctorArgs = type.AllInjectedFields.Select(f => f.CtorArg).ToArray();

        // Expected: deterministic, alphabetical-by-name ordering so that
        // generated code is byte-identical regardless of process-specific
        // hash codes.
        ctorArgs.ShouldBe(new[] { "alpha", "bravo", "charlie", "delta", "mango", "zebra" });
    }

    [Fact]
    public void generated_source_for_constructor_parameters_is_in_deterministic_sorted_order()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Runner", typeof(IRunOrderTest));
        var method = type.MethodFor(nameof(IRunOrderTest.Run));

        var call = new MethodCall(typeof(MultiArgRunHandler), nameof(MultiArgRunHandler.Run));
        call.Arguments[0] = new InjectedField(typeof(IZebraService), "zebra");
        call.Arguments[1] = new InjectedField(typeof(IAlphaService), "alpha");
        call.Arguments[2] = new InjectedField(typeof(IMangoService), "mango");
        call.Arguments[3] = new InjectedField(typeof(IBravoService), "bravo");
        call.Arguments[4] = new InjectedField(typeof(IDeltaService), "delta");
        call.Arguments[5] = new InjectedField(typeof(ICharlieService), "charlie");
        method.Frames.Add(call);

        var code = assembly.GenerateCode();
        _output.WriteLine(code);

        var ctorIndex = code.IndexOf("public Runner(", StringComparison.Ordinal);
        ctorIndex.ShouldBeGreaterThan(-1);

        var alphaIndex = code.IndexOf("alpha", ctorIndex, StringComparison.Ordinal);
        var bravoIndex = code.IndexOf("bravo", ctorIndex, StringComparison.Ordinal);
        var charlieIndex = code.IndexOf("charlie", ctorIndex, StringComparison.Ordinal);
        var deltaIndex = code.IndexOf("delta", ctorIndex, StringComparison.Ordinal);
        var mangoIndex = code.IndexOf("mango", ctorIndex, StringComparison.Ordinal);
        var zebraIndex = code.IndexOf("zebra", ctorIndex, StringComparison.Ordinal);

        alphaIndex.ShouldBeLessThan(bravoIndex);
        bravoIndex.ShouldBeLessThan(charlieIndex);
        charlieIndex.ShouldBeLessThan(deltaIndex);
        deltaIndex.ShouldBeLessThan(mangoIndex);
        mangoIndex.ShouldBeLessThan(zebraIndex);
    }

    [Fact]
    public void generated_source_is_byte_identical_when_frames_added_in_different_orders()
    {
        var code1 = generateWithFrames(forwardOrder: true);
        var code2 = generateWithFrames(forwardOrder: false);

        // The same logical set of injected fields should produce the same
        // exact source code regardless of the order frames were added.
        code1.ShouldBe(code2);
    }

    [Fact]
    public void generated_source_is_stable_across_repeated_invocations()
    {
        var first = generateWithFrames(forwardOrder: true);
        for (var i = 0; i < 5; i++)
        {
            var next = generateWithFrames(forwardOrder: true);
            next.ShouldBe(first);
        }
    }

    private static string generateWithFrames(bool forwardOrder)
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Runner", typeof(IRunOrderTest));
        var method = type.MethodFor(nameof(IRunOrderTest.Run));

        var call = new MethodCall(typeof(MultiArgRunHandler), nameof(MultiArgRunHandler.Run));
        var args = new Variable[]
        {
            new InjectedField(typeof(IZebraService), "zebra"),
            new InjectedField(typeof(IAlphaService), "alpha"),
            new InjectedField(typeof(IMangoService), "mango"),
            new InjectedField(typeof(IBravoService), "bravo"),
            new InjectedField(typeof(IDeltaService), "delta"),
            new InjectedField(typeof(ICharlieService), "charlie"),
        };

        if (!forwardOrder)
        {
            Array.Reverse(args);
            // Re-map argument indices so the method call still type-checks.
            // We need to put each variable into the parameter index that
            // matches its type.
            var parameters = typeof(MultiArgRunHandler).GetMethod(nameof(MultiArgRunHandler.Run))!.GetParameters();
            for (var i = 0; i < args.Length; i++)
            {
                var v = args[i];
                var idx = Array.FindIndex(parameters, p => p.ParameterType == v.VariableType);
                call.Arguments[idx] = v;
            }
        }
        else
        {
            call.Arguments[0] = args[0];
            call.Arguments[1] = args[1];
            call.Arguments[2] = args[2];
            call.Arguments[3] = args[3];
            call.Arguments[4] = args[4];
            call.Arguments[5] = args[5];
        }

        method.Frames.Add(call);

        return assembly.GenerateCode();
    }
}

public interface IRunOrderTest
{
    void Run();
}

public interface IAlphaService { }
public interface IBravoService { }
public interface ICharlieService { }
public interface IDeltaService { }
public interface IMangoService { }
public interface IZebraService { }

public static class MultiArgRunHandler
{
    public static void Run(
        IZebraService zebra,
        IAlphaService alpha,
        IMangoService mango,
        IBravoService bravo,
        IDeltaService delta,
        ICharlieService charlie)
    {
    }
}
