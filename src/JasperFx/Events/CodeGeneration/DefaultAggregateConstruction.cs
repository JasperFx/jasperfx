using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

public class DefaultAggregateConstruction: SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly Type _returnType;
    private Variable _event;

    public DefaultAggregateConstruction(Type returnType, GeneratedType generatedType)
    {
        _returnType = returnType;

        _constructor = returnType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);



        if (_constructor != null && !_constructor.IsPublic)
        {

        }
    }

    public IfStyle IfStyle { get; set; } = IfStyle.Else;

    public string AdditionalNoConstructorExceptionDetails { get; set; } = string.Empty;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _event = chain.FindVariable(typeof(IEvent));
        yield return _event;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        IfStyle.Open(writer, null);

        if (_constructor == null)
        {
            writer.WriteLine(
                $"throw new {typeof(InvalidOperationException).FullNameInCode()}($\"There is no default constructor for {_returnType.FullNameInCode()}{AdditionalNoConstructorExceptionDetails}.\");"
            );
        }
        else if (!_constructor.IsPublic)
        {
            writer.WriteLine($"return ({_returnType.FullNameInCode()}){typeof(Activator).FullNameInCode()}.{nameof(Activator.CreateInstance)}(typeof({_returnType.FullNameInCode()}), true);");
        }
        else
        {
            writer.WriteLine($"return new {_returnType.FullNameInCode()}();");
        }

        IfStyle.Close(writer);

        Next?.GenerateCode(method, writer);
    }
}
