namespace JasperFx.CodeGeneration.Model;

public interface IServiceVariableSource : IVariableSource
{
    void ReplaceVariables(IMethodVariables method);

    void StartNewType();
    void StartNewMethod();
}