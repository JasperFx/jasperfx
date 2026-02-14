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

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "JFXEVT003",
        title: "Projection is not partial",
        messageFormat: "Projection '{0}' is not partial; falling back to runtime expression compilation",
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
