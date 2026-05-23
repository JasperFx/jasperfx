using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

public class GetServiceFromScopedContainerFrame : SyncFrame
{
    private readonly Variable _scoped;
    private readonly object? _serviceKey;


    public GetServiceFromScopedContainerFrame(Variable scoped, Type serviceType, object? serviceKey = null)
    {
        if (scoped.VariableType != typeof(IServiceProvider))
        {
            throw new ArgumentOutOfRangeException(nameof(scoped),
                $"Wrong type for the variable. Expected {typeof(IServiceProvider).FullNameInCode()} but got {scoped.VariableType.FullNameInCode()}");
        }

        _scoped = scoped;
        _serviceKey = serviceKey;
        uses.Add(_scoped);

        Variable = new Variable(serviceType, this);
    }

    /// <summary>
    ///     <summary>
    ///         Optional code fragment to write at the beginning of this
    ///         type in code
    ///     </summary>
    public ICodeFragment? Header { get; set; }

    public Variable Variable { get; }


    /// <summary>
    ///     Add a single line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void Comment(string text)
    {
        Header = new OneLineComment(text);
    }

    /// <summary>
    ///     Add a multi line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void MultiLineComment(string text)
    {
        Header = new MultiLineComment(text);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Header != null)
        {
            writer.WriteLine("");
            Header.Write(writer);
        }

        if (_serviceKey != null)
        {
            // Keyed service resolved through service location. Without the key this would emit
            // GetRequiredService<T> and either throw or resolve the wrong registration. See GH-2878.
            writer.Write(
                $"var {Variable.Usage} = {typeof(ServiceProviderKeyedServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService)}<{Variable.VariableType.FullNameInCode()}>({_scoped.Usage}, {CodeFormatter.Write(_serviceKey)});");
        }
        else
        {
            writer.Write(
                $"var {Variable.Usage} = {typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{Variable.VariableType.FullNameInCode()}>({_scoped.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }
}