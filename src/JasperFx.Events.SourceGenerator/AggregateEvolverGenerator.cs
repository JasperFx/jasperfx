using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.Events.SourceGenerator;

[Generator]
public sealed class AggregateEvolverGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline 1: types with Apply/Create/Evolve methods
        var directCandidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidateClass(node),
            transform: static (ctx, ct) => AggregateAnalyzer.Analyze(ctx, ct))
            .Where(static info => info != null)
            .Collect();

        // Pipeline 2: aggregate types referenced by IRefersToAggregate parameters
        var refCandidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsMethodWithPossibleAggregateAttribute(node),
            transform: static (ctx, ct) => AnalyzeRefersToAggregateParameter(ctx, ct))
            .Where(static info => info != null)
            .Collect();

        var combined = directCandidates.Combine(refCandidates);
        context.RegisterSourceOutput(combined, static (spc, pair) => ExecuteCombined(spc, pair.Left, pair.Right));
    }

    private static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is ClassDeclarationSyntax classDecl)
        {
            // Classes: check for Apply, Create, ShouldDelete, Project, Transform, Evolve, EvolveAsync,
            // or ApplyAsync (for EventProjection with explicit override that needs type registration)
            return classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.ValueText is "Apply" or "Create" or "ShouldDelete" or "Project" or "Transform" or "Evolve" or "EvolveAsync" or "ApplyAsync");
        }

        if (node is RecordDeclarationSyntax recordDecl)
        {
            // Records: only check for Evolve/EvolveAsync (Apply/Create on records is handled at runtime)
            return recordDecl.Members.OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.ValueText is "Evolve" or "EvolveAsync");
        }

        return false;
    }

    /// <summary>
    /// Lightweight syntax check for methods with parameters that have attributes
    /// containing "Aggregate" in the name (catches ReadAggregate, WriteAggregate, etc.)
    /// </summary>
    private static bool IsMethodWithPossibleAggregateAttribute(SyntaxNode node)
    {
        if (node is not MethodDeclarationSyntax method) return false;

        foreach (var param in method.ParameterList.Parameters)
        {
            foreach (var attrList in param.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = attr.Name.ToString();
                    if (name.Contains("Aggregate")) return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// For pipeline 2: resolve IRefersToAggregate attributes on method parameters
    /// and analyze the parameter type as a self-aggregating aggregate type.
    /// </summary>
    private static CandidateInfo? AnalyzeRefersToAggregateParameter(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;

        foreach (var param in method.ParameterList.Parameters)
        {
            foreach (var attrList in param.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrSymbolInfo = ctx.SemanticModel.GetSymbolInfo(attr, ct);
                    if (attrSymbolInfo.Symbol is not IMethodSymbol attrCtor) continue;

                    var attrType = attrCtor.ContainingType;
                    if (!AggregateAnalyzer.ImplementsIRefersToAggregate(attrType)) continue;

                    // Get the parameter type (the aggregate type)
                    var paramSymbol = ctx.SemanticModel.GetDeclaredSymbol(param, ct) as IParameterSymbol;
                    if (paramSymbol?.Type is not INamedTypeSymbol aggregateType) continue;

                    // Unwrap nullable
                    if (aggregateType.IsGenericType &&
                        aggregateType.ConstructedFrom.ToDisplayString() == "System.Nullable<T>")
                    {
                        aggregateType = (INamedTypeSymbol)aggregateType.TypeArguments[0];
                    }

                    // Unwrap IEventStream<T>
                    if (aggregateType.IsGenericType &&
                        aggregateType.ConstructedFrom.ToDisplayString().Contains("IEventStream"))
                    {
                        aggregateType = (INamedTypeSymbol)aggregateType.TypeArguments[0];
                    }

                    return AggregateAnalyzer.AnalyzeRefersToAggregate(aggregateType, ct);
                }
            }
        }

        return null;
    }

    private static void ExecuteCombined(
        SourceProductionContext spc,
        ImmutableArray<CandidateInfo?> direct,
        ImmutableArray<CandidateInfo?> refs)
    {
        var seen = new System.Collections.Generic.HashSet<string>();

        // Pipeline 1 candidates have priority
        foreach (var info in direct)
        {
            if (info == null) continue;
            var key = info.ClassSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            seen.Add(key);
            Execute(spc, info);
        }

        // Pipeline 2: only for types not already handled by pipeline 1
        foreach (var info in refs)
        {
            if (info == null) continue;
            var key = info.ClassSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (seen.Add(key))
            {
                Execute(spc, info);
            }
        }
    }

    private static void Execute(SourceProductionContext context, CandidateInfo info)
    {
        switch (info.Mode)
        {
            case CandidateMode.PartialProjection:
                EmitPartialProjection(context, info);
                break;

            case CandidateMode.SelfAggregating:
                EmitSelfAggregating(context, info);
                break;

            case CandidateMode.SelfAggregatingEvolve:
                EmitSelfAggregatingEvolve(context, info);
                break;

            case CandidateMode.EventProjection:
                EmitEventProjection(context, info);
                break;

            case CandidateMode.EventProjectionTypeRegistrationOnly:
                EmitEventProjectionTypeRegistration(context, info);
                break;

            case CandidateMode.None:
                // Emit diagnostics for why we're skipping
                if (!info.IsPartial && info.ClassSymbol != null)
                {
                    // Check if it's a projection that could have been generated
                    var projBase = AggregateAnalyzer.FindAggregationProjectionBase(info.ClassSymbol);
                    if (projBase != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.NotPartial,
                            info.ClassSyntax.Identifier.GetLocation(),
                            info.ClassSymbol.Name));
                    }

                    // Check if it's an event projection that could have been generated
                    var eventProjBase = AggregateAnalyzer.FindEventProjectionBase(info.ClassSymbol);
                    if (eventProjBase != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.NotPartial,
                            info.ClassSyntax.Identifier.GetLocation(),
                            info.ClassSymbol.Name));
                    }
                }
                break;
        }
    }

    private static string SafeHintName(INamedTypeSymbol symbol, string suffix)
    {
        var fullName = symbol.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_').Replace('.', '_');
        return $"{fullName}{suffix}.g.cs";
    }

    private static void EmitPartialProjection(SourceProductionContext context, CandidateInfo info)
    {
        if (info.Methods.Count == 0) return;

        var source = EvolverCodeEmitter.EmitPartialProjection(info);
        context.AddSource(SafeHintName(info.ClassSymbol, ".Evolver"), source);
    }

    private static void EmitEventProjection(SourceProductionContext context, CandidateInfo info)
    {
        if (info.Methods.Count == 0) return;

        var source = EvolverCodeEmitter.EmitEventProjectionPartial(info);
        context.AddSource(SafeHintName(info.ClassSymbol, ".EventProjection"), source);
    }

    private static void EmitEventProjectionTypeRegistration(SourceProductionContext context, CandidateInfo info)
    {
        if (info.DiscoveredPublishedTypes.Count == 0) return;

        var source = EvolverCodeEmitter.EmitEventProjectionTypeRegistrationPartial(info);
        context.AddSource(SafeHintName(info.ClassSymbol, ".TypeRegistration"), source);
    }

    private static void EmitSelfAggregatingEvolve(SourceProductionContext context, CandidateInfo info)
    {
        if (info.EvolveMethod == null) return;

        // Must be able to infer identity
        if (info.IdentityType == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.CannotInferIdentity,
                info.ClassSyntax.Identifier.GetLocation(),
                info.ClassSymbol.Name));
            return;
        }

        var source = EvolverCodeEmitter.EmitSelfAggregatingEvolveEvolver(info);
        context.AddSource(SafeHintName(info.ClassSymbol, "EvolveEvolver"), source);
    }

    private static void EmitSelfAggregating(SourceProductionContext context, CandidateInfo info)
    {
        if (info.Methods.Count == 0) return;

        // Self-aggregating with async methods falls back
        if (info.HasAnyAsync)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AsyncSelfAggregating,
                info.ClassSyntax.Identifier.GetLocation(),
                info.ClassSymbol.Name));
            return;
        }

        // Must be able to infer identity
        if (info.IdentityType == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.CannotInferIdentity,
                info.ClassSyntax.Identifier.GetLocation(),
                info.ClassSymbol.Name));
            return;
        }

        var source = EvolverCodeEmitter.EmitSelfAggregatingEvolver(info);
        context.AddSource(SafeHintName(info.ClassSymbol, "Evolver"), source);
    }
}
