using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Plain text or markdown describing one element a slice references — a command, an event, a read model
/// (jasperfx#1298). See <see cref="EventModelSliceDescriptor.ElementDescriptions"/>; the slice's own
/// description is <see cref="EventModelSliceDescriptor.Description"/>.
/// </summary>
/// <remarks>
/// Declared with <c>Describe&lt;T&gt;("…")</c>; import fills it from board text; the derived rung fills it from
/// XML doc comments; stubs and specs carry it as comments. The model never carries agent-guidance sections.
/// On a merge the richer text wins and nothing is ever recorded as a disagreement.
/// </remarks>
/// <param name="Type">The described element, by type identity.</param>
/// <param name="Text">The description, plain text or markdown.</param>
public sealed record ElementDescription(TypeDescriptor Type, string Text);
