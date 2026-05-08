namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic descriptor for a single DCB tag-type registration, suitable
/// for monitoring tools rendering the configured tag surface of an event
/// store. Mirrors the shape of an <c>ITagTypeRegistration</c> without
/// dragging in runtime behaviour, so the descriptor can be safely
/// round-tripped over the wire.
/// </summary>
/// <param name="Name">Friendly name of the tag (typically the strong-typed identifier's short name).</param>
/// <param name="SimpleType">FullName of the inner primitive type the tag wraps (e.g. <c>"System.String"</c>, <c>"System.Guid"</c>).</param>
/// <param name="TagType">Type identity of the strong-typed tag itself.</param>
/// <param name="Description">Operator-facing description of the tag's purpose; <see langword="null"/> when no description was registered.</param>
public sealed record DcbTagDescriptor(
    string Name,
    string SimpleType,
    TypeDescriptor TagType,
    string? Description);
