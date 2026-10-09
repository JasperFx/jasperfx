namespace JasperFx.Events;

/// <summary>
/// Assembly-level manifest entry, emitted by the JasperFx.Events source generator, recording the
/// event types a handler method was inferred to append from its body. One entry per method.
/// </summary>
/// <remarks>
/// <para>
/// The generator infers from <see cref="IEventStream{T}.AppendOne" /> /
/// <see cref="IEventStream{T}.AppendMany(object[])" /> calls and from the code that builds an
/// <see cref="ICarriesEvents" /> value. An event it cannot see statically — one typed <c>object</c>,
/// say — is left out, so the manifest is a lower bound, never a complete list. Consumers that let
/// users declare emitted events explicitly should union the two.
/// </para>
/// <para>
/// Only <c>typeof</c> tokens are used, so reading the manifest is AOT-safe: one attribute lookup per
/// assembly. Overloads of one method name share entries keyed by that name. <c>AllowMultiple</c> is
/// also what keeps a double-loaded analyzer (jasperfx#462) from failing the build with CS0579.
/// See jasperfx#990.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class EmittedEventsAttribute : Attribute
{
    public EmittedEventsAttribute(Type handlerType, string methodName, params Type[] eventTypes)
    {
        HandlerType = handlerType;
        MethodName = methodName;
        EventTypes = eventTypes;
    }

    /// <summary>The type declaring the handler method.</summary>
    public Type HandlerType { get; }

    /// <summary>The handler method's name.</summary>
    public string MethodName { get; }

    /// <summary>The event types inferred from the method body, in order of first appearance.</summary>
    public Type[] EventTypes { get; }
}
