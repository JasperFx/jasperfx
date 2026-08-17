namespace JasperFx.Events;

/// <summary>
/// Marks an event type as binary-serialized: its payload is written through an
/// <see cref="IEventBinarySerializer" /> rather than the store's JSON path.
/// </summary>
/// <remarks>
/// <para>
/// Promoted alongside <see cref="IEventBinarySerializer" /> for the same reason and at the same
/// time. An attribute is applied to the event type itself, and event types are exactly what a
/// consumer compiling one body of source against several stores shares — so a store-namespaced
/// attribute is unusable in precisely the code that most wants it. Promoting the interface without
/// the attribute would have left the feature reachable store-agnostically through registration but
/// not through declaration.
/// </para>
/// <para>
/// The attribute only marks intent. The serializer is resolved by the store at registration time,
/// and every store that honors this attribute should resolve it the same way: an explicit per-type
/// registration wins, a store-wide default is the fallback, and a type marked here with neither
/// configured is a configuration error the store raises rather than a silent reversion to JSON.
/// Silently falling back to JSON would write rows in a format the operator did not choose and
/// believes they are not using.
/// </para>
/// <para>
/// Deliberately <b>not</b> sealed, which is the one thing about it that is not obvious. A store that
/// already shipped its own <c>BinaryEventAttribute</c> before this one existed cannot delete it
/// without breaking its users, and could not derive from a sealed one either (CS0509) — so it is
/// left resolving <em>two</em> attribute types on every event, forever. Marten is exactly that
/// store. Unsealing lets such a store make its own attribute a subclass of this one and collapse the
/// check back to a single lookup, because
/// <see cref="System.Reflection.CustomAttributeExtensions.GetCustomAttribute{T}(System.Reflection.MemberInfo)" />
/// matches a derived attribute. See <see href="https://github.com/JasperFx/jasperfx/issues/672" />.
/// </para>
/// <para>
/// Stores with no pre-existing attribute of their own should use this one directly rather than
/// subclassing it; a store-namespaced subclass would reintroduce the very thing promoting the
/// attribute was meant to remove, which is an attribute a consumer sharing event types across stores
/// cannot apply.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public class BinaryEventAttribute : Attribute;
