using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace JasperFx.Events.CodeGeneration;

internal class CreateMethodFrame: MethodCall, IEventHandlingFrame
{
    private readonly Type _operationsType;
    private static int _counter;

    private Variable _operations;

    public CreateMethodFrame(Type operationsType, MethodSlot slot): base(slot.HandlerType, (MethodInfo)slot.Method)
    {
        _operationsType = operationsType;
        EventType = Method.GetEventType(null);
        ReturnVariable.OverrideName(ReturnVariable.Usage + ++_counter);
    }

    public Type EventType { get; }

    public void Configure(EventProcessingFrame parent)
    {
        // Replace any arguments to IEvent<T>
        TrySetArgument(parent.SpecificEvent);

        // Replace any arguments to the specific T event type
        TrySetArgument(parent.DataOnly);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in base.FindVariables(chain)) yield return variable;

        _operations = chain.FindVariable(_operationsType);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        base.GenerateCode(method, writer);
        writer.WriteLine($"{_operations.Usage}.Store({ReturnVariable.Usage});");
    }
}
