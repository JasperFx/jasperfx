namespace JasperFx.CodeGeneration.Model;

public interface IServiceVariableSource : IVariableSource
{
    void ReplaceVariables(IMethodVariables method);

    void StartNewType();
    void StartNewMethod();

    bool TryFindKeyedService(Type type, string key, out Variable? variable);
}