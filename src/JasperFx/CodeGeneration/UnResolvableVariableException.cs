using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

public class UnResolvableVariableException : Exception
{
    public UnResolvableVariableException(Type dependencyType, string variableName, IGeneratedMethod method)
    {
        DependencyType = dependencyType;
        VariableName = variableName;
        Method = method;
    }

    public UnResolvableVariableException(Type dependencyType, IGeneratedMethod method)
    {
        DependencyType = dependencyType;
        Method = method;
    }

    public Type DependencyType { get; }
    public string? VariableName { get; }
    public IGeneratedMethod Method { get; }

    public GeneratedType? Type { get; set; }

    public override string Message
    {
        get
        {
            var methodName = Type == null ? Method.ToString() : $"{Type.TypeName}.{Method}";

            if (VariableName.IsNotEmpty())
            {
                var message = $"JasperFx was unable to resolve a variable of type {DependencyType.FullNameInCode()} with name '{VariableName}' as part of the method {methodName}";
                
                if (!DependencyType.IsValueType)
                {
                    message += ". If this parameter is optional in certain contexts (e.g., when used as both an HTTP endpoint and message handler), consider making it nullable by using '?' (e.g., HttpContext? httpContext).";
                }
                
                return message;
            }

            return
                $"JasperFx was unable to resolve a variable of type {DependencyType.FullNameInCode()} as part of the method {methodName}";
        }
    }
}