using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.FSharp.Core;
using Shouldly;

namespace CodegenTests;

/// <summary>
///     Tests for <see cref="ConstructorFrame.FSharpInvocation" /> covering the two branches:
///     <list type="bullet">
///       <item>Non-F#-record types use positional constructor syntax: <c>TypeName(arg1, arg2)</c></item>
///       <item>F# record types use record-expression syntax: <c>{ Field = val; ... }</c></item>
///     </list>
///     The F# record branch was added to fix "FS1133: No constructors available" errors that occur
///     when generated F# code uses positional construction on an F# record type.
/// </summary>
public class FSharpRecordConstructorFrameTests
{
    [Fact]
    public void non_record_type_uses_positional_constructor_call_syntax()
    {
        var ctor = typeof(MultiArgTarget).GetConstructors().Single();
        var frame = new ConstructorFrame(ctor);
        frame.Parameters[0] = Variable.For<int>("count");
        frame.Parameters[1] = Variable.For<string>("name");

        frame.FSharpInvocation()
            .ShouldBe("CodegenTests.FSharpRecordConstructorFrameTests.MultiArgTarget(count, name)");
    }

    [Fact]
    public void fsharp_record_type_uses_record_expression_syntax()
    {
        // FSharpRef<string> is the F# record `type 'T ref = { mutable contents: 'T }`.
        // Its single constructor parameter is named "contents"; IsFSharpRecord() returns true.
        // FSharpInvocation() must emit the F# record-expression form { Contents = arg }
        // instead of the positional call FSharpRef<string>(arg), which the F# compiler rejects.
        var ctor = typeof(FSharpRef<string>).GetConstructors().Single();
        var frame = new ConstructorFrame(ctor);
        frame.Parameters[0] = Variable.For<string>("myValue");

        frame.FSharpInvocation().ShouldBe("{ Contents = myValue }");
    }

    public class MultiArgTarget
    {
        public MultiArgTarget(int count, string name)
        {
            Count = count;
            Name = name;
        }

        public int Count { get; }
        public string Name { get; }
    }
}
