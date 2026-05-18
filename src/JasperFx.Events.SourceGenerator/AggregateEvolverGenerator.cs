using System;
using System.Collections.Generic;
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

        // Pipeline 3: aggregate types registered via `Snapshot<T>(...)` / `LiveStreamAggregation<T>(...)` /
        // `SingleStreamProjection<T>(...)` call sites. Marten's `opts.Projections.Snapshot<T>(...)` etc.
        // instantiate the framework-side `SingleStreamProjection<T, TId>` directly, so there is no user
        // subclass for pipeline 1 to find. The aggregate type itself still owns the Apply/Create methods,
        // so we analyze it as a self-aggregating candidate keyed off the call-site type argument. See #293.
        var snapshotCandidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsSnapshotRegistrationInvocation(node),
            transform: static (ctx, ct) => AnalyzeSnapshotInvocation(ctx, ct))
            .Where(static info => info != null)
            .Collect();

        var combined = directCandidates.Combine(refCandidates).Combine(snapshotCandidates);
        context.RegisterSourceOutput(combined,
            static (spc, triple) => ExecuteCombined(spc, triple.Left.Left, triple.Left.Right, triple.Right));
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
    /// Method names that register a self-aggregating snapshot/projection keyed on a single
    /// generic aggregate type argument. The intent is to catch consumer code like:
    ///   <c>opts.Projections.Snapshot&lt;TAggregate&gt;(...)</c>
    ///   <c>opts.Projections.LiveStreamAggregation&lt;TAggregate&gt;(...)</c>
    ///   <c>opts.Projections.SingleStreamProjection&lt;TAggregate&gt;(...)</c>
    /// We match by simple name only — semantic-model resolution happens in
    /// <see cref="AnalyzeSnapshotInvocation"/>, so a same-named user helper that isn't a
    /// Marten/JasperFx registration is naturally filtered by the analysis step. See #293.
    /// </summary>
    private static readonly HashSet<string> SnapshotRegistrationNames = new()
    {
        "Snapshot",
        "LiveStreamAggregation",
        "SingleStreamProjection",
    };

    /// <summary>
    /// Lightweight syntax check: does <paramref name="node"/> look like a generic
    /// method invocation whose simple name is one of the snapshot/self-aggregating
    /// registration entry points? Final filtering happens in <see cref="AnalyzeSnapshotInvocation"/>.
    /// </summary>
    private static bool IsSnapshotRegistrationInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation) return false;

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name,
            GenericNameSyntax g => g,
            _ => null
        };

        if (name is not GenericNameSyntax generic) return false;
        if (generic.TypeArgumentList.Arguments.Count != 1) return false;

        return SnapshotRegistrationNames.Contains(generic.Identifier.ValueText);
    }

    /// <summary>
    /// Pipeline 3: resolve <c>Snapshot&lt;T&gt;()</c> (and equivalents) call sites,
    /// extract the aggregate type argument, and analyze it as self-aggregating.
    /// </summary>
    private static CandidateInfo? AnalyzeSnapshotInvocation(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        var generic = invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name as GenericNameSyntax,
            GenericNameSyntax g => g,
            _ => null
        };

        if (generic is null) return null;
        if (generic.TypeArgumentList.Arguments.Count != 1) return null;

        var typeArgSyntax = generic.TypeArgumentList.Arguments[0];
        if (ctx.SemanticModel.GetSymbolInfo(typeArgSyntax, ct).Symbol is not INamedTypeSymbol aggregateType)
            return null;

        // Resolve the invoked method and require it to be defined on a JasperFx.Events
        // or Marten projections-collection-style API. The conservative check: the
        // containing namespace path must include "JasperFx.Events" or "Marten". This
        // prevents matching a same-named helper in unrelated user code that happens to
        // have a single generic type argument named Snapshot/LiveStreamAggregation/etc.
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return null;

        if (!IsKnownSnapshotApi(method)) return null;

        return AggregateAnalyzer.AnalyzeRefersToAggregate(aggregateType, ct);
    }

    private static bool IsKnownSnapshotApi(IMethodSymbol method)
    {
        // Walk the containing-type chain and check whether any owning namespace is a
        // JasperFx.Events / Marten projections surface. We don't require an exact
        // type match because the registration entry point can live on several
        // user-facing collection types (e.g. Marten's ProjectionOptions).
        var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns.StartsWith("Marten", StringComparison.Ordinal)
               || ns.StartsWith("JasperFx.Events", StringComparison.Ordinal);
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
        ImmutableArray<CandidateInfo?> refs,
        ImmutableArray<CandidateInfo?> snapshots)
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

        // Pipeline 3: only for types not already handled by pipelines 1 or 2
        foreach (var info in snapshots)
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
