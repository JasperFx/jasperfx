using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

internal class DeclareAggregateFrame: SyncFrame
{
    public DeclareAggregateFrame(Type aggregateType)
    {
        Variable = new Variable(aggregateType, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{Variable.VariableType.FullNameInCode()} {Variable.Usage} = null;");
        Next?.GenerateCode(method, writer);
    }
}
