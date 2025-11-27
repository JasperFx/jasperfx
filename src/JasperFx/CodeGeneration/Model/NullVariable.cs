namespace JasperFx.CodeGeneration.Model;

/// <summary>
/// Represents a variable that will be resolved as null at runtime
/// </summary>
public class NullVariable : Variable
{
    public NullVariable(Type variableType, string usage) : base(variableType, usage)
    {

    }

    public override string ArgumentDeclaration => "null";
}
