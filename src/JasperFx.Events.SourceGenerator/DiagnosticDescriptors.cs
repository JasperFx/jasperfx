using Microsoft.CodeAnalysis;

namespace JasperFx.Events.SourceGenerator;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor AsyncSelfAggregating = new(
        id: "JFXEVT001",
        title: "Self-aggregating type has async methods",
        messageFormat: "Self-aggregating type '{0}' has async methods; falling back to runtime expression compilation",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotInferIdentity = new(
        id: "JFXEVT002",
        title: "Cannot infer identity type",
        messageFormat: "Cannot infer identity type for '{0}'; add [AggregateIdentity] or an Id property",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The projection has conventional methods the generator cannot dispatch without a second
    /// declaration of the user's class. There is no runtime fallback for this — the projection throws
    /// <c>InvalidProjectionException</c> when the store is built (see
    /// <c>JasperFxAggregationProjectionBase.AssembleAndAssertValidity</c>) — so this is an error rather
    /// than the Info-level "falling back to runtime expression compilation" note it used to be. That
    /// message described the pre-2.0 FEC fallback, which the 9.0 projections rework deleted, and Info
    /// severity kept it out of CLI builds entirely.
    /// </summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "JFXEVT003",
        title: "Projection must be declared partial",
        messageFormat:
            "No dispatcher can be generated for projection '{0}' because {1} — {2}. Conventional Apply/Create/ShouldDelete methods are dispatched by the compile-time source generator and there is no runtime fallback.",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
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

    public static readonly DiagnosticDescriptor HasLambdaRegistrations = new(
        id: "JFXEVT004",
        title: "Has lambda registrations",
        messageFormat: "Skipping '{0}' — has lambda registrations in constructor",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
