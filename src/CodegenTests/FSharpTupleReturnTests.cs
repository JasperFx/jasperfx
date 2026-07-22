using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Shouldly;

namespace CodegenTests;

/// <summary>
///     Tests for the F# tuple destructuring emitted by <c>MethodCall.fsharpDetermineReturnValue</c>.
///     F# uses different syntax depending on tuple kind and binding context:
///     <list type="bullet">
///       <item>Sync, any tuple: <c>let (a, b) = expr</c> — struct keyword is optional in sync context</item>
///       <item>Async task-CE, struct tuple: <c>let! struct (a, b) = expr</c> — struct required</item>
///       <item>Async task-CE, reference tuple: <c>let! (a, b) = expr</c></item>
///     </list>
///     Both <c>System.ValueTuple&lt;&gt;</c> and <c>System.Tuple&lt;&gt;</c> pass
///     <c>IsValueTuple()</c>, so the codegen uses <c>Type.IsValueType</c> to distinguish them —
///     but only in the async binding path where the distinction matters to the F# compiler.
/// </summary>
public class FSharpTupleReturnTests
{
    [Fact]
    public void sync_struct_tuple_emits_struct_keyword()
    {
        // F# requires `struct` in the destructuring pattern for value tuples in ALL contexts
        // (sync and async). `let (a, b) = structTupleValue` causes FS0001.
        var call = new MethodCall(typeof(TupleHelpers), nameof(TupleHelpers.ReturnsStructTuple));
        var writer = new SourceWriter();
        call.GenerateFSharpCode(GeneratedMethod.ForNoArg("Test"), writer);

        var code = writer.Code();
        code.ShouldContain("let struct (");
    }

    [Fact]
    public void sync_reference_tuple_does_not_emit_struct_keyword()
    {
        var call = new MethodCall(typeof(TupleHelpers), nameof(TupleHelpers.ReturnsReferenceTuple));
        var writer = new SourceWriter();
        call.GenerateFSharpCode(GeneratedMethod.ForNoArg("Test"), writer);

        var code = writer.Code();
        code.ShouldContain("let (");
        code.ShouldNotContain("let struct (");
    }

    public static class TupleHelpers
    {
        public static (string Name, int Count) ReturnsStructTuple() => ("x", 1);
        public static Tuple<string, int> ReturnsReferenceTuple() => Tuple.Create("x", 1);
    }
}
