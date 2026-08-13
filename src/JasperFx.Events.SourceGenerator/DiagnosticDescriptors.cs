using Microsoft.CodeAnalysis;

namespace JasperFx.Events.SourceGenerator;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor CannotInferIdentity = new(
        id: "JFXEVT002",
        title: "Cannot infer identity type",
        messageFormat: "Cannot infer identity type for '{0}'; add [AggregateIdentity] or an Id property",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "JFXEVT003",
        title: "Projection is not partial",
        messageFormat: "Projection '{0}' is not partial; falling back to runtime expression compilation",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>
    /// An EventProjection's explicit ApplyAsync writes a document whose type the generator cannot
    /// name, so no published-type registration is emitted for it. Silent otherwise: the store will
    /// still provision that document's storage on demand, and only the ahead-of-time surfaces
    /// (schema creation, known document types, rebuild teardown) come up short.
    /// </summary>
    public static readonly DiagnosticDescriptor UnregistrableDocumentOperation = new(
        id: "JFXEVT005",
        title: "Document type cannot be registered from this operation",
        messageFormat:
            "'{0}' calls {1} on its session with document type '{2}', which cannot be registered as a published type; call it with a concrete document type so the projection registers it",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>
    /// An EventProjection with an explicit <c>ApplyAsync</c> writes documents the generator can name,
    /// but the registration is a <c>PublishedTypes()</c> override emitted into the projection class, so
    /// a non-partial projection cannot receive one. Warning rather than error: nothing fails at runtime,
    /// the store provisions that document's storage on demand, and only the ahead-of-time surfaces
    /// (schema creation, known document types, rebuild teardown) come up short. Before this the skip was
    /// entirely silent. See #654.
    /// </summary>
    public static readonly DiagnosticDescriptor UnregisteredPublishedTypes = new(
        id: "JFXEVT006",
        title: "Published document types cannot be registered",
        messageFormat:
            "'{0}' writes document type(s) {1} from its ApplyAsync override, but is not declared partial, so they are not registered as published types; declare the projection partial, or register them with RegisterPublishedType",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
