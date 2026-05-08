using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Per-slice fluent builder. Each method describes one role inside a
/// slice (trigger, command, handler, emitted events, projections,
/// read models) and returns <c>this</c> so the author can chain them
/// into a single declarative block.
/// </summary>
public class EventModelSliceBuilder
{
    private readonly string _sliceName;
    private string? _triggerLabel;
    private TypeDescriptor? _triggerType;
    private TypeDescriptor? _commandType;
    private TypeDescriptor? _handlerType;
    private readonly List<TypeDescriptor> _emittedEvents = new();
    private readonly List<TypeDescriptor> _projectionTypes = new();
    private readonly List<TypeDescriptor> _readModelTypes = new();

    /// <summary>
    /// Create a slice builder. The discovery layer wires this up — author
    /// code never instantiates it directly.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder(string sliceName)
    {
        _sliceName = sliceName;
    }

    /// <summary>
    /// Declare a free-form trigger label (e.g. "User clicks Save"). Use
    /// when the trigger is not represented by a CLR type.
    /// </summary>
    /// <param name="label">Display label for the trigger.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy(string label)
    {
        _triggerLabel = label;
        return this;
    }

    /// <summary>
    /// Declare a CLR-typed trigger (e.g. an inbound HTTP request DTO).
    /// </summary>
    /// <typeparam name="T">CLR trigger type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy<T>()
    {
        _triggerType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>
    /// Declare the command type that this slice dispatches.
    /// </summary>
    /// <typeparam name="T">CLR command type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Command<T>()
    {
        _commandType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>
    /// Declare the handler / aggregate type that processes the slice's
    /// command.
    /// </summary>
    /// <typeparam name="T">CLR handler type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder HandledBy<T>()
    {
        _handlerType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>
    /// Declare an event type that this slice emits. Call once per emitted
    /// event.
    /// </summary>
    /// <typeparam name="T">CLR event type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Emits<T>()
    {
        _emittedEvents.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>
    /// Declare a projection that consumes events from this slice. Call
    /// once per projection.
    /// </summary>
    /// <typeparam name="T">CLR projection type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Projects<T>()
    {
        _projectionTypes.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>
    /// Declare a read model that this slice reads from. Call once per
    /// read model.
    /// </summary>
    /// <typeparam name="T">CLR read-model type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Reads<T>()
    {
        _readModelTypes.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    internal EventModelSliceDescriptor Build()
        => new(
            _sliceName,
            _triggerLabel,
            _triggerType,
            _commandType,
            _handlerType,
            _emittedEvents.ToList(),
            _projectionTypes.ToList(),
            _readModelTypes.ToList());
}
