using System.Globalization;
using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

/// <summary>
/// Service plan for constructor parameters with default values that cannot be
/// resolved from DI. Emits the parameter's compile-time default value as a
/// constant in the generated code.
/// </summary>
internal class DefaultValuePlan : ServicePlan
{
    private readonly ParameterInfo _parameter;

    public DefaultValuePlan(ParameterInfo parameter)
        : base(new ServiceDescriptor(parameter.ParameterType, _ => null!, ServiceLifetime.Transient))
    {
        _parameter = parameter;
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        var usage = FormatDefaultValue(_parameter);
        return new Variable(_parameter.ParameterType, usage);
    }

    protected override bool requiresServiceProvider(IMethodVariables method) => false;

    public override string WhyRequireServiceProvider(IMethodVariables method) => string.Empty;

    internal static string FormatDefaultValue(ParameterInfo parameter)
    {
        var defaultValue = parameter.DefaultValue;
        var parameterType = parameter.ParameterType;

        if (defaultValue == null)
        {
            return $"default({parameterType.FullNameInCode()})";
        }

        if (parameterType == typeof(bool))
        {
            return (bool)defaultValue ? "true" : "false";
        }

        if (parameterType == typeof(string))
        {
            return $"\"{defaultValue}\"";
        }

        if (parameterType == typeof(char))
        {
            return $"'{defaultValue}'";
        }

        if (parameterType == typeof(float))
        {
            return ((float)defaultValue).ToString(CultureInfo.InvariantCulture) + "f";
        }

        if (parameterType == typeof(double))
        {
            return ((double)defaultValue).ToString(CultureInfo.InvariantCulture) + "d";
        }

        if (parameterType == typeof(decimal))
        {
            return ((decimal)defaultValue).ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (parameterType == typeof(long))
        {
            return defaultValue.ToString() + "L";
        }

        if (parameterType.IsEnum)
        {
            return $"{parameterType.FullNameInCode()}.{defaultValue}";
        }

        // Numeric types (int, short, byte, etc.)
        return defaultValue.ToString()!;
    }
}
