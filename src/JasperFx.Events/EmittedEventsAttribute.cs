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
/// assembly. <c>AllowMultiple</c> is also what keeps a double-loaded analyzer (jasperfx#462) from
/// failing the build with CS0579. See jasperfx#990.
/// </para>
/// <para>
/// Overloads of one method name get one entry each, told apart by <see cref="ParameterTypes" />
/// (jasperfx#1021). An overload whose parameter types cannot all be named by a <c>typeof</c> at
/// assembly scope — a private nested type, a <c>file</c>-local type, a <c>ref</c> or <c>out</c>
/// parameter — is recorded without <see cref="ParameterTypes" />, and such entries for one name are
/// merged, as every overload was before. A consumer matching a method should use the entries whose
/// <see cref="ParameterTypes" /> equal the method's parameter types and, only when there are none,
/// the entries that have no <see cref="ParameterTypes" />. A consumer that ignores
/// <see cref="ParameterTypes" /> sees the union of the overloads' events under the name.
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

    /// <summary>
    /// The handler method's parameter types, in declaration order, so overloads of one name can be told
    /// apart. Null when the generator could not name them all, and in manifests written before
    /// jasperfx#1021.
    /// </summary>
    public Type[]? ParameterTypes { get; set; }
}
