using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.Events.SourceGenerator;

[Generator]
public sealed class AggregateEvolverGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidateClass(node),
            transform: static (ctx, ct) => AggregateAnalyzer.Analyze(ctx, ct))
            .Where(static info => info != null);

        context.RegisterSourceOutput(candidates, static (spc, info) => Execute(spc, info!));
    }

    private static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl) return false;

        // Must have at least one method named Apply, Create, or ShouldDelete
        return classDecl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.ValueText is "Apply" or "Create" or "ShouldDelete");
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
