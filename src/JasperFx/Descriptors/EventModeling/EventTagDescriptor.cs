using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// One consistency tag of the model — an entry in the model-level tag catalogue
/// (<see cref="EventModelDescriptor.Tags"/>, jasperfx#1297). Mirrors <see cref="AggregateDescriptor"/>:
/// a name, the strong-typed tag type when one exists, the primitive inside it, and the rung that said so.
/// </summary>
/// <remarks>
/// <para>
/// Only <em>consistency</em> tags are modelled, the ones a Dynamic Consistency Boundary query selects
/// events by. Informational labels never enter the descriptor (Sekiban's <c>IsConsistencyTag</c>,
/// Chronicle's "custom tags do not take part").
/// </para>
/// <para>
/// Declared with <c>EventModelBuilder.Tag&lt;CourseId&gt;()</c> or <c>Tag("courseId", typeof(Guid))</c>; a tag an
/// event is <c>TaggedBy</c> on some slice joins the catalogue on its own. The derived rung fills it from the
/// store's tag registry (Wolverine's side).
/// </para>
/// </remarks>
/// <param name="Name">The tag's name — what a tag value is written under, e.g. <c>courseId</c>.</param>
/// <param name="Type">The strong-typed tag type (<c>CourseId</c>), or null when the tag has none yet.</param>
public sealed record EventTagDescriptor(string Name, TypeDescriptor? Type)
{
    /// <summary>
    /// The primitive inside the tag type — the <c>Guid</c> in <c>CourseId(Guid Value)</c> — or null when
    /// nothing said. The registry knows it; a declaration may say it with <c>Tag("courseId", typeof(Guid))</c>.
    /// </summary>
    public TypeDescriptor? InnerType { get; init; }

    /// <summary>
    /// Which rung of the provenance ladder described this tag. Null means unattributed, read as
    /// <see cref="EventModelProvenance.Declared"/>. Stamped by <see cref="EventModelDescriptor.WithProvenance"/>.
    /// </summary>
    public EventModelProvenance? Provenance { get; init; }

    /// <summary>
    /// The default tag name for a tag type: its simple name in camelCase, so <c>CourseId</c> is
    /// <c>courseId</c>. One rule, used by the fluent API and by any source that names a tag from a type.
    /// </summary>
    public static string NameFor(Type tagType) => NameFor(tagType.Name);

    /// <summary>The default tag name for a tag type's simple name: <c>CourseId</c> becomes <c>courseId</c>.</summary>
    public static string NameFor(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("A tag needs a name", nameof(typeName));
        }

        var backtick = typeName.IndexOf('`');
        if (backtick > 0) typeName = typeName[..backtick];

        return typeName.Length == 1
            ? typeName.ToLowerInvariant()
            : char.ToLowerInvariant(typeName[0]) + typeName[1..];
    }
}
