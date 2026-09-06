using JasperFx.CodeGeneration.Model;
using JasperFx.Core;

namespace JasperFx.CodeGeneration.Frames;

/// <summary>
///     A frame that generates no behavior. Its reason to exist is a generated method whose *only*
///     payload is its attributes — the AOT rooting companion emitted by
///     <see cref="AotRootsCompanionExtensions.AddAotRoots(GeneratedAssembly, string, IEnumerable{AttributeArg})" />
///     being the motivating case. C# tolerates a truly empty block; F# does not, so the F# emit
///     writes the unit value.
/// </summary>
public class NoOpFrame : SyncFrame
{
    public NoOpFrame(string? comment = null)
    {
        Comment = comment;
    }

    /// <summary>
    ///     Optional single line comment explaining why the body is empty.
    /// </summary>
    public string? Comment { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Comment.IsNotEmpty())
        {
            writer.WriteComment(Comment!);
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Comment.IsNotEmpty())
        {
            writer.WriteComment(Comment!);
        }

        // F# has no empty body; `()` is the unit value that stands in for one.
        writer.WriteLine("()");

        Next?.GenerateFSharpCode(method, writer);
    }
}
