using System.Reflection;
using System.Reflection.Emit;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.FSharp.Core;
using Shouldly;

namespace CodegenTests;

/// <summary>
///     Tests for <c>MethodCall.GenerateFSharpCode</c> when the target method carries
///     <see cref="CompilationArgumentCountsAttribute" /> — the F# compiler's marker for class
///     members that have multiple separate parameter groups (curried class members), e.g.:
///     <code>static member Apply (a: string) (b: int) = ...</code>
///     Without the fix, these would be emitted with C#-style parentheses <c>Apply(a, b)</c>,
///     causing FS0001 type errors at compile time. The correct F# call syntax is space-separated:
///     <c>Apply a b</c>.
/// </summary>
public class FSharpCurriedMemberTests
{
    /// <summary>
    ///     Builds a dynamic type with a static method that carries
    ///     <see cref="CompilationArgumentCountsAttribute" /> — mimicking what the F# compiler emits
    ///     for <c>static member Apply (a: string) (b: int) = ...</c>.
    /// </summary>
    private static (Type OwnerType, MethodInfo Method) CreateCurriedStaticMethod()
    {
        var asmName = new AssemblyName("DynamicCurriedTest_" + Guid.NewGuid().ToString("N"));
        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        var modBuilder = asmBuilder.DefineDynamicModule("Main");
        var typeBuilder = modBuilder.DefineType("CurriedHelper",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        var methodBuilder = typeBuilder.DefineMethod(
            "Apply",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(string),
            new[] { typeof(string), typeof(int) });

        // Decorate with CompilationArgumentCountsAttribute([1; 1]):
        // two parameter groups, each of arity 1 — identical to what the F# compiler emits for
        // `static member Apply (a: string) (b: int) = ...`.
        var attrCtor = typeof(CompilationArgumentCountsAttribute)
            .GetConstructor(new[] { typeof(int[]) })!;
        methodBuilder.SetCustomAttribute(
            new CustomAttributeBuilder(attrCtor, new object[] { new int[] { 1, 1 } }));

        var il = methodBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        var dynamicType = typeBuilder.CreateType();
        return (dynamicType, dynamicType.GetMethod("Apply")!);
    }

    [Fact]
    public void curried_class_member_uses_space_separated_argument_syntax()
    {
        var (ownerType, method) = CreateCurriedStaticMethod();

        var call = new MethodCall(ownerType, method);
        call.Arguments[0] = Variable.For<string>("str");
        call.Arguments[1] = Variable.For<int>("num");

        var writer = new SourceWriter();
        call.GenerateFSharpCode(GeneratedMethod.ForNoArg("Test"), writer);
        var code = writer.Code();

        // Curried (space-separated) call syntax — the F# compiler requires this for methods
        // carrying CompilationArgumentCountsAttribute.
        code.ShouldContain("Apply str num");
        // Must NOT emit parenthesised C#-style call: Apply(str, num) would fail with FS0001.
        code.ShouldNotContain("Apply(str, num)");
    }

    [Fact]
    public void non_curried_method_uses_parenthesised_argument_syntax()
    {
        // Baseline: an ordinary static method (no CompilationArgumentCountsAttribute)
        // should still use the parenthesised call syntax.
        var call = new MethodCall(typeof(PlainHelper), nameof(PlainHelper.Combine));
        call.Arguments[0] = Variable.For<string>("str");
        call.Arguments[1] = Variable.For<int>("num");

        var writer = new SourceWriter();
        call.GenerateFSharpCode(GeneratedMethod.ForNoArg("Test"), writer);
        var code = writer.Code();

        code.ShouldContain("Combine(str, num)");
    }

    public static class PlainHelper
    {
        public static string Combine(string str, int num) => str + num;
    }
}
