using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration.Frames;

public class IfBlock : CompositeFrame
{
    public IfBlock(string condition, params Frame[] inner) : base(inner)
    {
        Condition = condition;
    }

    public IfBlock(Variable variable, params Frame[] inner) : this(variable.Usage, inner)
    {
        uses.Add(variable);
    }

    public string Condition { get; }

    protected override void generateCode(GeneratedMethod method, ISourceWriter writer, Frame inner)
    {
        writer.Write($"BLOCK:if ({Condition})");
        inner.GenerateCode(method, writer);
        writer.FinishBlock();
    }

    protected override void generateFSharpCode(GeneratedMethod method, ISourceWriter writer, Frame inner)
    {
        // F#: `if <condition> then` with an indented (brace-free) body. The Condition string is a
        // raw passthrough (like CodeFrame), so the caller supplies F#-valid text; an IfBlock built
        // from a Variable uses the bare identifier, which is already valid. The inner must be a
        // unit-typed expression (a side effect) when the if-block is not the trailing expression.
        writer.Write($"BLOCK:if {Condition} then");
        inner.GenerateFSharpCode(method, writer);
        writer.FinishBlock();
    }
}