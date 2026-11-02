using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// One more way into a slice, beyond the one its <see cref="EventModelSliceDescriptor.TriggerKind"/> and
/// <see cref="EventModelSliceDescriptor.HandlerType"/> name (jasperfx#1099). See
/// <see cref="EventModelSliceDescriptor.AdditionalEntryPoints"/>.
/// </summary>
/// <param name="Kind">How the code receives the command through this entry point.</param>
/// <param name="HandlerType">The user's class behind this entry point, when the source named one.</param>
/// <param name="HandlerMethod">The user's method on <paramref name="HandlerType"/>, when the source named one.</param>
/// <param name="Origin">The source that described this entry point, when it attributed itself.</param>
public sealed record EventModelEntryPoint(
    TriggerKind Kind,
    TypeDescriptor? HandlerType,
    string? HandlerMethod,
    Uri? Origin);
