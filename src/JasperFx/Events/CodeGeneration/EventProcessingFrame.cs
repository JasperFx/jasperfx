using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

public interface IEventHandlingFrame
{
    Type EventType { get; }
    void Configure(EventProcessingFrame parent);
}

/// <summary>
///     Organizes a single Event type within a pattern
///     matching switch statement
/// </summary>
public class EventProcessingFrame: Frame
{
    private static int _counter;
    protected readonly IList<Frame> _inner = new List<Frame>();
    private Variable _event;

    public EventProcessingFrame(bool isAsync, Type aggregateType, Type eventType): base(isAsync)
    {
        EventType = eventType;
        AggregateType = aggregateType;

        SpecificEvent = new Variable(typeof(IEvent<>).MakeGenericType(eventType),
            "event_" + eventType.Name.Sanitize() + ++_counter);
        DataOnly = new Variable(EventType, $"{SpecificEvent.Usage}.{nameof(IEvent<string>.Data)}");
    }

    public EventProcessingFrame(Type aggregateType, IEventHandlingFrame inner)
        : this(inner.As<Frame>().IsAsync, aggregateType, inner.EventType)
    {
        Add(inner.As<Frame>());
    }

    public Type AggregateType { get; }

    public Type EventType { get; }


    public Variable SpecificEvent { get; }

    public Variable Aggregate { get; private set; }

    public Variable DataOnly { get; }

    public void Add(Frame inner)
    {
        _inner.Add(inner);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (AggregateType != null)
        {
            // You don't need it if you're in a Create method
            Aggregate = chain.TryFindVariable(AggregateType, VariableSource.All);
            if (Aggregate != null)
            {
                yield return Aggregate;
            }
        }

        foreach (var inner in _inner.OfType<IEventHandlingFrame>()) inner.Configure(this);

        _event = chain.FindVariable(typeof(IEvent));

        yield return _event;

        foreach (var inner in _inner)
        {
            foreach (var variable in inner.FindVariables(chain)) yield return variable;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"case {SpecificEvent.VariableType.FullNameInCode()} {SpecificEvent.Usage}:");

        writer.IndentionLevel++;

        foreach (var frame in _inner) frame.GenerateCode(method, writer);

        writer.Write("break;");
        writer.IndentionLevel--;

        Next?.GenerateCode(method, writer);
    }
}
