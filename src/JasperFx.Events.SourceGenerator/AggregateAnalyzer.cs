using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.Events.SourceGenerator;

internal enum CandidateMode
{
    None,
    PartialProjection,
    SelfAggregating,
    SelfAggregatingEvolve,
    EventProjection,
    /// <summary>
    /// EventProjection that already has an explicit ApplyAsync override.
    /// Only needs published type registration, not method generation.
    /// See https://github.com/JasperFx/marten/issues/4166
    /// </summary>
    EventProjectionTypeRegistrationOnly,

    /// <summary>
    /// A type marked [BoundaryAggregate] that the generator cannot emit an evolver for, because it
    /// declares neither an Evolve method nor any conventional Apply/Create methods. Carried through
    /// the pipeline solely so the generator can report JFXEVT007 — nothing is emitted for it.
    /// See https://github.com/JasperFx/jasperfx/issues/730
    /// </summary>
    InvalidBoundaryAggregate
}

internal sealed class ConventionalMethodInfo
{
    public string MethodName { get; set; } = ""; // "Apply", "Create", "ShouldDelete"
    public IMethodSymbol Symbol { get; set; } = null!;
    public ITypeSymbol EventType { get; set; } = null!;
    public bool UsesIEventWrapper { get; set; }
    public bool UsesRawIEvent { get; set; }
    public bool IsAsync { get; set; }
    public bool ReturnsAggregate { get; set; }
    public bool IsStatic { get; set; }
    public bool IsOnAggregate { get; set; } // true if declared on the aggregate type itself
    public bool HasSession { get; set; }
    public bool HasCancellationToken { get; set; }
    public bool IsVoid { get; set; }
    // Parameter order tracking
    public bool IsAggregateFirst { get; set; } // Apply(aggregate, event) vs Apply(event, aggregate)
    // EventProjection-specific
    public bool HasOperations { get; set; }
    public ITypeSymbol? EntityReturnType { get; set; } // For Create/Transform: the unwrapped return type
}

/// <summary>
/// Describes a user-defined Evolve or EvolveAsync method on a self-aggregating type.
/// </summary>
internal sealed class EvolveMethodInfo
{
    public IMethodSymbol Symbol { get; set; } = null!;
    public bool IsAsync { get; set; }
    /// <summary>True if the method takes IEvent, false if it takes object (e.Data)</summary>
    public bool TakesIEvent { get; set; }
    /// <summary>True if void/Task return (mutable), false if returns TDoc/ValueTask&lt;TDoc&gt; (immutable)</summary>
    public bool IsMutable { get; set; }
    /// <summary>True if the method has an IQuerySession parameter</summary>
    public bool HasSession { get; set; }
    /// <summary>True if the method has a CancellationToken parameter</summary>
    public bool HasCancellationToken { get; set; }
    /// <summary>Concrete event types extracted from method body (switch/case, is patterns)</summary>
    public List<ITypeSymbol> ExtractedEventTypes { get; set; } = new();
}

/// <summary>
/// Records a public single-argument constructor on an aggregate type whose
/// parameter is the implicit "create" event. The pre-#276 Marten reflection
/// runtime would call `new TAggregate(eventData)` for the first event of
/// the matching type; the SG emits the same call via a switch arm.
/// </summary>
internal sealed class EventConstructorInfo
{
    public ITypeSymbol EventType { get; set; } = null!;
    public IMethodSymbol Ctor { get; set; } = null!;
}

internal sealed class CandidateInfo
{
    public CandidateMode Mode { get; set; }
    public INamedTypeSymbol ClassSymbol { get; set; } = null!;
    public TypeDeclarationSyntax ClassSyntax { get; set; } = null!;
    public bool IsPartial { get; set; }
    public INamedTypeSymbol? AggregateType { get; set; } // TDoc
    public INamedTypeSymbol? IdentityType { get; set; } // TId
    public INamedTypeSymbol? QuerySessionType { get; set; } // TQuerySession
    public List<ConventionalMethodInfo> Methods { get; set; } = new();
    public bool HasShouldDelete => Methods.Any(m => m.MethodName == "ShouldDelete") || ConstructorDeleteEventTypes.Count > 0;
    // A method that takes IQuerySession can only run from the EvolveAsync /
    // DetermineActionAsync path (the sync Evolve doesn't have a session to
    // pass). Treat any session-taking handler as forcing the async dispatch
    // path so the generated switch arm can bind `session` correctly. See #297.
    public bool HasAnyAsync => Methods.Any(m => m.IsAsync || m.HasSession);
    public bool HasCreate => Methods.Any(m => m.MethodName == "Create");

    /// <summary>
    /// Event types registered via the kept `DeleteEvent&lt;T&gt;()` parameterless
    /// API in the projection's constructor. The emitter adds one switch arm
    /// per type that sets `snapshot = null` to trigger a delete. Per the Marten
    /// 9.0 / JasperFx#276 chip, this constructor-side registration is one of
    /// the explicitly-kept ways to mark an event type as a delete trigger.
    /// </summary>
    public List<ITypeSymbol> ConstructorDeleteEventTypes { get; set; } = new();

    /// <summary>
    /// Public single-argument constructors on the aggregate whose parameter
    /// looks like an event type. The emitter treats each as an implicit
    /// Create handler — `case TEvent data: return new TAggregate(data);` —
    /// preserving the pre-#276 reflection behavior. See #297.
    /// </summary>
    public List<EventConstructorInfo> EventConstructors { get; set; } = new();
    public bool HasDefaultConstructor { get; set; }
    public bool HasExistingParameterlessConstructor { get; set; } // On the projection class itself

    /// <summary>
    /// True when the projection — or a user-written base class between it and ProjectionBase —
    /// already overrides PublishedTypes(). The generator yields to a hand-written override rather
    /// than emitting a second one and tripping CS0111. See marten#5192.
    /// </summary>
    public bool HasExistingPublishedTypesOverride { get; set; }

    // EventProjection-specific
    public INamedTypeSymbol? OperationsType { get; set; } // TOperations from JasperFxEventProjectionBase<TOperations, TQuerySession>
    /// <summary>
    /// Document types discovered from Store/Insert/Delete calls in method bodies.
    /// Used for EventProjectionTypeRegistrationOnly mode.
    /// See https://github.com/JasperFx/marten/issues/4166
    /// </summary>
    public List<ITypeSymbol> DiscoveredPublishedTypes { get; set; } = new();

    /// <summary>
    /// Document operations that bound to the projection's own session but whose document type is
    /// not registrable (object, dynamic, an open type parameter). Reported as JFXEVT005 rather than
    /// silently skipped, because nothing at the call site tells the user it was skipped.
    /// </summary>
    public List<UnresolvedDocumentOperation> UnresolvedDocumentOperations { get; set; } = new();
    // SelfAggregatingEvolve-specific
    public EvolveMethodInfo? EvolveMethod { get; set; }
    // Natural key discovery
    public bool HasNaturalKey { get; set; }
}

/// <summary>
/// A document operation that bound to an EventProjection's own session but whose document type
/// could not be turned into a published-type registration.
/// </summary>
internal sealed class UnresolvedDocumentOperation
{
    public UnresolvedDocumentOperation(string methodName, string documentTypeDisplay, Location location)
    {
        MethodName = methodName;
        DocumentTypeDisplay = documentTypeDisplay;
        Location = location;
    }

    public string MethodName { get; }
    public string DocumentTypeDisplay { get; }
    public Location Location { get; }
}

internal static class AggregateAnalyzer
{
    private const string AggregationProjectionBaseFullName =
        "JasperFx.Events.Aggregation.JasperFxAggregationProjectionBase";

    private const string EventProjectionBaseFullName =
        "JasperFx.Events.Projections.JasperFxEventProjectionBase";

    private const string ProjectionBaseFullName = "JasperFx.Events.Projections.ProjectionBase";

    public static CandidateInfo? Analyze(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = context.Node as TypeDeclarationSyntax;
        if (classDecl == null) return null;
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (classSymbol == null) return null;

        // Determine if this is a projection subclass or self-aggregating
        var projBaseInfo = FindAggregationProjectionBase(classSymbol);

        if (projBaseInfo != null)
        {
            return AnalyzeProjectionSubclass(classDecl, classSymbol, projBaseInfo.Value, context.SemanticModel, ct);
        }

        // Check if this is an EventProjection subclass
        var eventProjBaseInfo = FindEventProjectionBase(classSymbol);
        if (eventProjBaseInfo != null)
        {
            return AnalyzeEventProjectionSubclass(classDecl, classSymbol, eventProjBaseInfo.Value,
                context.SemanticModel, ct);
        }

        // Not a projection subclass - check if self-aggregating
        if (!IsProjectionBase(classSymbol))
        {
            return AnalyzeSelfAggregating(classDecl, classSymbol, context.SemanticModel, ct);
        }

        return null;
    }

    private static CandidateInfo? AnalyzeProjectionSubclass(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        (INamedTypeSymbol docType, INamedTypeSymbol idType, INamedTypeSymbol querySessionType) baseInfo,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        var isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);

        // Check if it already overrides Evolve/EvolveAsync/DetermineAction/DetermineActionAsync
        if (HasExplicitOverride(classSymbol))
            return null;

        var methods = DiscoverConventionalMethods(classSymbol, baseInfo.docType, classSymbol);
        var ctorDeleteTypes = DiscoverConstructorDeleteEventTypes(classDecl, semanticModel, ct);

        if (methods.Count == 0 && ctorDeleteTypes.Count == 0) return null;

        return new CandidateInfo
        {
            // NOT gated on `partial`. The dispatcher for an aggregation projection subclass is emitted
            // as a standalone `file sealed class` registered through [assembly: GeneratedEvolver(...)]
            // (#462) — it calls the projection's public conventional methods from the outside and never
            // needs a second declaration of the user's type. The only emission that still injects members
            // into the user's class is the DI-override path (marten#4787), which checks IsPartial itself
            // and reports JFXEVT003 when the modifier is missing. Gating candidacy on the modifier was
            // left over from the pre-#462 member-injection emission and silently skipped every
            // non-partial projection, surfacing much later as "No source-generated dispatcher found".
            Mode = CandidateMode.PartialProjection,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = isPartial,
            ConstructorDeleteEventTypes = ctorDeleteTypes,
            AggregateType = baseInfo.docType,
            IdentityType = baseInfo.idType,
            QuerySessionType = baseInfo.querySessionType,
            Methods = methods,
            HasDefaultConstructor = HasParameterlessConstructor(baseInfo.docType),
            HasExistingParameterlessConstructor = HasExplicitParameterlessConstructor(classSymbol),
            HasExistingPublishedTypesOverride = HasPublishedTypesOverride(classSymbol)
        };
    }

    private static CandidateInfo? AnalyzeSelfAggregating(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        // Check for Evolve/EvolveAsync methods first — these take priority
        var evolveResult = AnalyzeSelfAggregatingEvolve(classDecl, classSymbol, semanticModel, ct);
        if (evolveResult != null) return evolveResult;

        var methods = DiscoverConventionalMethods(classSymbol, classSymbol, null);
        var eventCtors = DiscoverEventConstructors(classSymbol);
        if (methods.Count == 0 && eventCtors.Count == 0)
        {
            // Nothing to fold events with. Silent for an ordinary type, but [BoundaryAggregate] is an
            // explicit opt-in, so a type carrying it and generating nothing is unambiguously wrong and
            // the runtime failure lands far away (#730).
            return HasBoundaryAggregateAttribute(classSymbol)
                ? new CandidateInfo
                {
                    Mode = CandidateMode.InvalidBoundaryAggregate,
                    ClassSymbol = classSymbol,
                    ClassSyntax = classDecl,
                    IsPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
                    AggregateType = classSymbol
                }
                : null;
        }

        // Infer TId from Id property or AggregateIdentityAttribute
        var idType = InferIdentityType(classSymbol);
        if (idType == null)
        {
            // Identity-less "boundary" aggregate (DCB): no Id and no [AggregateIdentity],
            // so there's nothing to key a single stream on. We only emit an evolver when
            // the type opts in with [BoundaryAggregate] — a bare no-Id aggregate is far
            // more likely a mistake (forgot the Id) than an intentional boundary aggregate,
            // and we don't want to silently swallow that. When opted in, default TId to
            // string to match the SingleStreamProjection<T, string> the DCB aggregator
            // builds; the id is vestigial for boundary dispatch. See #324.
            if (!HasBoundaryAggregateAttribute(classSymbol)) return null;
            idType = semanticModel.Compilation.GetSpecialType(SpecialType.System_String);
        }

        return new CandidateInfo
        {
            Mode = CandidateMode.SelfAggregating,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
            AggregateType = classSymbol,
            IdentityType = idType,
            Methods = methods,
            EventConstructors = eventCtors,
            HasDefaultConstructor = HasParameterlessConstructor(classSymbol),
            HasNaturalKey = HasNaturalKeyProperty(classSymbol)
        };
    }

    /// <summary>
    /// Find public single-argument constructors on the aggregate whose
    /// parameter is an event type (i.e. matches an event type referenced by
    /// any of the aggregate's Apply methods, or is referenced by name from
    /// an enclosing test fixture's event list). Pre-#276 Marten reflectively
    /// invoked these as implicit Create handlers; we preserve that
    /// behavior by emitting `new T(eventData)` in the null-snapshot
    /// branch for each registered event type. See #297.
    ///
    /// The conservative heuristic here is "param is not a framework type and
    /// the type-arg looks like a user-defined class/record/struct" — that's
    /// the same shape the previous reflection path accepted. Filtering by
    /// "must match an Apply event type" would be stricter, but Marten's
    /// existing tests register aggregates whose first event ONLY arrives via
    /// the ctor (no separate Apply for that event type), so we deliberately
    /// don't require a matching Apply.
    /// </summary>
    private static List<EventConstructorInfo> DiscoverEventConstructors(INamedTypeSymbol type)
    {
        var result = new List<EventConstructorInfo>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.Parameters.Length != 1) continue;
            if (ctor.DeclaredAccessibility != Accessibility.Public) continue;

            var paramType = ctor.Parameters[0].Type;
            if (IsFrameworkType(paramType)) continue;

            // Skip if the param looks like IEvent / IEvent<T> — those are
            // handled by the regular Apply/Create method discovery.
            if (IsIEventGenericWrapper(paramType, out _)) continue;
            if (IsRawIEvent(paramType)) continue;

            if (seen.Add(paramType))
            {
                result.Add(new EventConstructorInfo { EventType = paramType, Ctor = ctor });
            }
        }

        return result;
    }

    private static CandidateInfo? AnalyzeSelfAggregatingEvolve(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        var evolveMethod = FindEvolveMethod(classSymbol);
        if (evolveMethod == null) return null;

        var idType = InferIdentityType(classSymbol);
        if (idType == null)
        {
            // Identity-less "boundary" aggregate (DCB), same opt-in as the Apply/Create path below.
            // This branch used to be a bare `return null`, which made an Evolve-only boundary
            // aggregate unreachable: the Evolve path bailed here, and the Apply/Create path — the
            // only one that knew about [BoundaryAggregate] — then returned null at its "no
            // conventional methods" guard before ever reaching the opt-in. So the attribute was
            // honoured for Apply/Create and silently ignored for Evolve, and the consequence did not
            // surface until FetchForWritingByTags<T> threw "No source-generated dispatcher found" at
            // fetch time. See #730.
            if (!HasBoundaryAggregateAttribute(classSymbol)) return null;
            idType = semanticModel.Compilation.GetSpecialType(SpecialType.System_String);
        }

        // Extract event types from the method body
        var methodSyntax = evolveMethod.Symbol.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax(ct) as MethodDeclarationSyntax;

        if (methodSyntax != null)
        {
            ExtractEventTypesFromBody(methodSyntax, classSymbol, evolveMethod);
        }

        return new CandidateInfo
        {
            Mode = CandidateMode.SelfAggregatingEvolve,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
            AggregateType = classSymbol,
            IdentityType = idType,
            HasDefaultConstructor = HasParameterlessConstructor(classSymbol),
            EvolveMethod = evolveMethod,
            HasNaturalKey = HasNaturalKeyProperty(classSymbol)
        };
    }

    private static EvolveMethodInfo? FindEvolveMethod(INamedTypeSymbol classSymbol)
    {
        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;
            if (method.MethodKind != MethodKind.Ordinary) continue;
            if (method.Name is not ("Evolve" or "EvolveAsync")) continue;
            if (method.DeclaredAccessibility != Accessibility.Public) continue;
            if (HasJasperFxIgnoreAttribute(method)) continue;

            var info = new EvolveMethodInfo { Symbol = method };
            var returnType = method.ReturnType;
            info.IsAsync = IsAsyncReturnType(returnType);

            if (info.IsAsync)
            {
                var unwrapped = UnwrapTaskType(returnType);
                // Task (mutable) or ValueTask<TDoc> (immutable)
                info.IsMutable = unwrapped == null ||
                                 unwrapped.SpecialType == SpecialType.System_Void;
            }
            else
            {
                // void (mutable) or TDoc (immutable)
                info.IsMutable = returnType.SpecialType == SpecialType.System_Void;
            }

            // Analyze parameters
            foreach (var param in method.Parameters)
            {
                var paramType = param.Type;

                if (IsRawIEvent(paramType))
                {
                    info.TakesIEvent = true;
                }
                else if (paramType.ToDisplayString() == "object")
                {
                    info.TakesIEvent = false;
                }
                else if (IsQuerySession(paramType))
                {
                    info.HasSession = true;
                }
                else if (IsCancellationToken(paramType))
                {
                    info.HasCancellationToken = true;
                }
            }

            return info;
        }

        return null;
    }

    /// <summary>
    /// Extracts concrete event types from the body of an Evolve/EvolveAsync method
    /// by scanning for switch/case patterns and is-type checks.
    /// </summary>
    private static void ExtractEventTypesFromBody(
        MethodDeclarationSyntax methodSyntax,
        INamedTypeSymbol classSymbol,
        EvolveMethodInfo evolveMethod)
    {
        if (methodSyntax.Body == null && methodSyntax.ExpressionBody == null) return;

        // We need to find type references in patterns. Since we're in a source generator
        // without a semantic model for the method body, we look for syntax patterns:
        // - case IEvent<T> varName:  (switch case with declaration pattern)
        // - case T varName:  (switch case with declaration pattern on e.Data)
        // - is IEvent<T> varName  (is-type expression)
        // - is T varName  (is-type expression on e.Data)
        //
        // We collect the type syntax nodes and resolve them later via the evolver's
        // EventTypes property generation.

        var typeNames = new HashSet<string>();

        var descendants = methodSyntax.DescendantNodes();
        foreach (var node in descendants)
        {
            switch (node)
            {
                // case SomeType varName: or case SomeType:
                case CasePatternSwitchLabelSyntax casePattern:
                    CollectTypesFromPattern(casePattern.Pattern, typeNames);
                    break;

                // C# 8+ switch expression arms: SomeType x =>
                case SwitchExpressionArmSyntax arm:
                    CollectTypesFromPattern(arm.Pattern, typeNames);
                    break;

                // is SomeType varName
                case IsPatternExpressionSyntax isPattern:
                    CollectTypesFromPattern(isPattern.Pattern, typeNames);
                    break;

                // is SomeType (without pattern, older C# style)
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression):
                    if (binary.Right is TypeSyntax typeSyntax)
                    {
                        var typeName = ExtractTypeNameFromTypeSyntax(typeSyntax);
                        if (typeName != null) typeNames.Add(typeName);
                    }
                    break;
            }
        }

        // Now resolve the type names against the compilation
        var compilation = classSymbol.ContainingAssembly;
        foreach (var tn in typeNames)
        {
            // Try to find the type symbol from all referenced types
            // For IEvent<T>, extract T
            var resolvedType = TryResolveEventType(tn, classSymbol);
            if (resolvedType != null)
            {
                evolveMethod.ExtractedEventTypes.Add(resolvedType);
            }
        }
    }

    private static void CollectTypesFromPattern(PatternSyntax pattern, HashSet<string> typeNames)
    {
        switch (pattern)
        {
            case DeclarationPatternSyntax decl:
            {
                var typeName = ExtractTypeNameFromTypeSyntax(decl.Type);
                if (typeName != null) typeNames.Add(typeName);
                break;
            }
            case RecursivePatternSyntax recursive when recursive.Type != null:
            {
                var typeName = ExtractTypeNameFromTypeSyntax(recursive.Type);
                if (typeName != null) typeNames.Add(typeName);
                break;
            }
            case ConstantPatternSyntax:
                // Skip constant patterns
                break;
        }
    }

    private static string? ExtractTypeNameFromTypeSyntax(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            GenericNameSyntax generic => generic.ToString(),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.ToString(),
            _ => null
        };
    }

    private static ITypeSymbol? TryResolveEventType(string typeName, INamedTypeSymbol contextType)
    {
        // For IEvent<T> patterns, we want to extract T as the event type
        if (typeName.StartsWith("IEvent<") && typeName.EndsWith(">"))
        {
            var innerTypeName = typeName.Substring("IEvent<".Length, typeName.Length - "IEvent<".Length - 1);
            return FindTypeByName(innerTypeName, contextType);
        }

        // For direct type patterns (on e.Data), use as-is
        var resolved = FindTypeByName(typeName, contextType);
        // Skip well-known non-event types
        if (resolved != null && !IsFrameworkType(resolved))
            return resolved;

        return null;
    }

    private static ITypeSymbol? FindTypeByName(string name, INamedTypeSymbol contextType)
    {
        // Search in the same namespace first
        var ns = contextType.ContainingNamespace;
        if (ns != null)
        {
            var found = FindTypeInNamespace(name, ns);
            if (found != null) return found;
        }

        // Search in the containing assembly's global namespace
        var globalNs = contextType.ContainingAssembly?.GlobalNamespace;
        if (globalNs != null)
        {
            var found = FindTypeRecursive(name, globalNs);
            if (found != null) return found;
        }

        return null;
    }

    private static ITypeSymbol? FindTypeInNamespace(string name, INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.Name == name) return type;
        }
        return null;
    }

    private static ITypeSymbol? FindTypeRecursive(string name, INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.Name == name) return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            var found = FindTypeRecursive(name, childNs);
            if (found != null) return found;
        }

        return null;
    }

    private static List<ConventionalMethodInfo> DiscoverConventionalMethods(
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol aggregateType,
        INamedTypeSymbol? projectionType)
    {
        var methods = new List<ConventionalMethodInfo>();

        // Search on the aggregate type itself
        DiscoverMethodsOnType(aggregateType, aggregateType, true, methods);

        // Search on the projection type if different
        if (projectionType != null && !SymbolEqualityComparer.Default.Equals(projectionType, aggregateType))
        {
            DiscoverMethodsOnType(projectionType, aggregateType, false, methods);
        }

        return methods;
    }

    private static void DiscoverMethodsOnType(
        INamedTypeSymbol searchType,
        INamedTypeSymbol aggregateType,
        bool isOnAggregate,
        List<ConventionalMethodInfo> results)
    {
        // Walk the inheritance chain so aggregates / projections that inherit
        // Apply/Create/ShouldDelete from a user base class still get those
        // methods dispatched. Stop at System.Object and at any framework base
        // (ProjectionBase / JasperFxAggregationProjectionBase /
        // JasperFxEventProjectionBase) so we don't slurp framework internals.
        // See https://github.com/JasperFx/jasperfx/issues/295.
        var seenSignatures = new HashSet<string>();
        for (INamedTypeSymbol? current = searchType;
             current is not null
             && current.SpecialType != SpecialType.System_Object
             && !IsFrameworkBase(current);
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IMethodSymbol method) continue;
                if (method.MethodKind != MethodKind.Ordinary) continue;
                if (method.Name is not ("Apply" or "Create" or "ShouldDelete")) continue;
                if (HasJasperFxIgnoreAttribute(method)) continue;
                // Skip non-public methods - we can't call them from generated code
                if (method.DeclaredAccessibility != Accessibility.Public) continue;

                // Walking derived → base, the first sighting of a given signature wins.
                // That gives a derived `override` or `new` declaration priority over
                // the base-class declaration with the same signature, matching the
                // language's own method-resolution behavior.
                if (!seenSignatures.Add(SignatureKey(method))) continue;

                var info = AnalyzeMethod(method, aggregateType, isOnAggregate);
                if (info != null)
                {
                    results.Add(info);
                }
            }
        }
    }

    private static bool IsFrameworkBase(INamedTypeSymbol type)
    {
        var fullName = type.ConstructedFrom?.ToDisplayString() ?? type.ToDisplayString();
        return fullName.StartsWith(ProjectionBaseFullName, StringComparison.Ordinal)
               || fullName.StartsWith(AggregationProjectionBaseFullName, StringComparison.Ordinal)
               || fullName.StartsWith(EventProjectionBaseFullName, StringComparison.Ordinal);
    }

    private static string SignatureKey(IMethodSymbol method)
    {
        return method.Name + "("
               + string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))
               + ")";
    }

    private static ConventionalMethodInfo? AnalyzeMethod(
        IMethodSymbol method,
        INamedTypeSymbol aggregateType,
        bool isOnAggregate)
    {
        var parameters = method.Parameters;
        if (parameters.Length == 0) return null;

        var info = new ConventionalMethodInfo
        {
            MethodName = method.Name,
            Symbol = method,
            IsStatic = method.IsStatic,
            IsOnAggregate = isOnAggregate
        };

        // Determine return type
        var returnType = method.ReturnType;
        info.IsAsync = IsAsyncReturnType(returnType);

        if (info.IsAsync)
        {
            var unwrapped = UnwrapTaskType(returnType);
            info.ReturnsAggregate = unwrapped != null &&
                                    SymbolEqualityComparer.Default.Equals(unwrapped, aggregateType);
            info.IsVoid = unwrapped == null; // Task with no result
        }
        else
        {
            info.ReturnsAggregate = SymbolEqualityComparer.Default.Equals(returnType, aggregateType);
            info.IsVoid = returnType.SpecialType == SpecialType.System_Void;

            if (method.Name == "ShouldDelete" && returnType.SpecialType != SpecialType.System_Boolean)
                return null;
        }

        // Find the event type parameter
        ITypeSymbol? eventType = null;
        bool foundAggregateFirst = false;
        bool aggregateSeen = false;

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.Type;

            // A parameter whose type is the aggregate type is the aggregate
            // slot. For projection-side methods (!isOnAggregate) this is the
            // shape `Apply(SomeEvent, TAggregate)`. For self-aggregating
            // methods we also accept the same shape via static helpers
            // (e.g. `static TAggregate Apply(SomeEvent, TAggregate current)`
            // on a record). Without this branch the second param falls
            // through to the "unrecognized parameter" bail below. See #297.
            if (SymbolEqualityComparer.Default.Equals(paramType, aggregateType))
            {
                aggregateSeen = true;
                if (eventType == null)
                    foundAggregateFirst = true;
                continue;
            }

            if (IsIEventGenericWrapper(paramType, out var wrappedEventType))
            {
                info.UsesIEventWrapper = true;
                eventType = wrappedEventType;
                if (aggregateSeen && !foundAggregateFirst)
                    foundAggregateFirst = false;
                continue;
            }

            if (IsRawIEvent(paramType))
            {
                info.UsesRawIEvent = true;
                continue;
            }

            if (IsQuerySession(paramType))
            {
                info.HasSession = true;
                continue;
            }

            if (IsCancellationToken(paramType))
            {
                info.HasCancellationToken = true;
                continue;
            }

            // This must be the event type
            if (eventType == null)
            {
                eventType = paramType;
                if (aggregateSeen)
                    foundAggregateFirst = true;
                else
                    foundAggregateFirst = false;
            }
            else
            {
                // An unrecognized parameter that comes after we've already
                // identified the event type — e.g. `Apply(AEvent, MyAggregate, IDocumentOperations)`
                // on a SingleStreamProjection where IDocumentOperations isn't a
                // supported convention slot. Bail rather than emit a garbled
                // dispatcher (CS1503 cascades from this). Runtime validation
                // already covers these "invalid signature" shapes — letting
                // the SG opt out preserves that behavior without breaking the
                // build. See #297.
                return null;
            }
        }

        if (eventType == null) return null;

        info.EventType = eventType;
        info.IsAggregateFirst = foundAggregateFirst;

        return info;
    }

    private static bool IsAsyncReturnType(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString();
        return fullName.StartsWith("System.Threading.Tasks.Task") ||
               fullName.StartsWith("System.Threading.Tasks.ValueTask");
    }

    private static ITypeSymbol? UnwrapTaskType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var fullName = named.ConstructedFrom.ToDisplayString();
            if (fullName == "System.Threading.Tasks.Task<TResult>" ||
                fullName == "System.Threading.Tasks.ValueTask<TResult>")
            {
                return named.TypeArguments[0];
            }
        }

        return null;
    }

    private static bool IsIEventGenericWrapper(ITypeSymbol type, out ITypeSymbol? eventType)
    {
        eventType = null;
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var constructedFrom = named.ConstructedFrom.ToDisplayString();
            if (constructedFrom == "JasperFx.Events.IEvent<T>")
            {
                eventType = named.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private static bool IsRawIEvent(ITypeSymbol type)
    {
        return type.ToDisplayString() == "JasperFx.Events.IEvent";
    }

    private static bool IsQuerySession(ITypeSymbol type)
    {
        // Check if the type is or extends IQuerySession-like types
        var name = type.ToDisplayString();
        return name.EndsWith("IQuerySession") || name.EndsWith("QuerySession");
    }

    private static bool IsCancellationToken(ITypeSymbol type)
    {
        return type.ToDisplayString() == "System.Threading.CancellationToken";
    }

    private static bool HasJasperFxIgnoreAttribute(IMethodSymbol method)
    {
        return method.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "JasperFxIgnoreAttribute" or "JasperFxIgnore");
    }

    /// <summary>
    /// Walks the base type chain to find JasperFxAggregationProjectionBase&lt;TDoc, TId, TOperations, TQuerySession&gt;
    /// </summary>
    public static (INamedTypeSymbol docType, INamedTypeSymbol idType, INamedTypeSymbol querySessionType)?
        FindAggregationProjectionBase(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            var fullName = current.ConstructedFrom.ToDisplayString();
            if (fullName.StartsWith(AggregationProjectionBaseFullName + "<"))
            {
                if (current.TypeArguments.Length >= 4
                    && current.TypeArguments[0] is INamedTypeSymbol docType
                    && current.TypeArguments[1] is INamedTypeSymbol idType
                    && current.TypeArguments[3] is INamedTypeSymbol querySessionType)
                {
                    // Only return concrete (INamedTypeSymbol) instantiations. An
                    // open generic base — e.g. `class MyBase<T> : SingleStreamProjection<T, Guid>`
                    // declared inside the JasperFx.Events runtime library itself —
                    // has TypeParameterSymbol type arguments which can't be cast
                    // to INamedTypeSymbol and previously crashed the SG with
                    // InvalidCastException. See #297.
                    return (docType, idType, querySessionType);
                }
            }

            current = current.BaseType;
        }

        return null;
    }

    private static bool IsProjectionBase(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString().StartsWith(ProjectionBaseFullName))
                return true;
            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Walks the base type chain to find JasperFxEventProjectionBase&lt;TOperations, TQuerySession&gt;
    /// </summary>
    public static (INamedTypeSymbol operationsType, INamedTypeSymbol querySessionType)?
        FindEventProjectionBase(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            var fullName = current.ConstructedFrom.ToDisplayString();
            if (fullName.StartsWith(EventProjectionBaseFullName + "<"))
            {
                if (current.TypeArguments.Length >= 2)
                {
                    return (
                        (INamedTypeSymbol)current.TypeArguments[0],
                        (INamedTypeSymbol)current.TypeArguments[1]
                    );
                }
            }

            current = current.BaseType;
        }

        return null;
    }

    private static CandidateInfo? AnalyzeEventProjectionSubclass(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        (INamedTypeSymbol operationsType, INamedTypeSymbol querySessionType) baseInfo,
        SemanticModel? semanticModel,
        CancellationToken ct)
    {
        var isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);

        // Check if it already overrides ApplyAsync
        if (HasApplyAsyncOverride(classSymbol))
        {
            // Even with an explicit override, we should still discover document types
            // used in Store/Insert/Delete calls so they can be registered as published types.
            // See https://github.com/JasperFx/marten/issues/4166
            //
            // Discovery runs whether or not the class is `partial`. The registration itself is a
            // PublishedTypes() override emitted into the user's class, so a non-partial projection
            // genuinely cannot have one — but bailing out here made that the one skip in the whole
            // generator that produced no diagnostic at all. The candidate is built either way and
            // EmitEventProjectionTypeRegistration reports JFXEVT006 instead of emitting. See #654.
            var unresolved = new List<UnresolvedDocumentOperation>();
            var discoveredTypes = DiscoverDocumentTypesFromMethodBodies(classDecl, classSymbol,
                baseInfo.operationsType, semanticModel, unresolved);

            // A class with nothing registrable AND nothing to warn about is not a candidate at all.
            if (discoveredTypes.Count == 0 && unresolved.Count == 0) return null;

            return new CandidateInfo
            {
                Mode = CandidateMode.EventProjectionTypeRegistrationOnly,
                ClassSymbol = classSymbol,
                ClassSyntax = classDecl,
                IsPartial = isPartial,
                OperationsType = baseInfo.operationsType,
                QuerySessionType = baseInfo.querySessionType,
                DiscoveredPublishedTypes = discoveredTypes,
                UnresolvedDocumentOperations = unresolved,
                HasExistingParameterlessConstructor = HasExplicitParameterlessConstructor(classSymbol),
            HasExistingPublishedTypesOverride = HasPublishedTypesOverride(classSymbol)
            };
        }

        var methods = DiscoverEventProjectionMethods(classSymbol, baseInfo.operationsType);

        if (methods.Count == 0) return null;

        return new CandidateInfo
        {
            Mode = isPartial ? CandidateMode.EventProjection : CandidateMode.None,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = isPartial,
            OperationsType = baseInfo.operationsType,
            QuerySessionType = baseInfo.querySessionType,
            Methods = methods,
            HasExistingParameterlessConstructor = HasExplicitParameterlessConstructor(classSymbol),
            HasExistingPublishedTypesOverride = HasPublishedTypesOverride(classSymbol)
        };
    }

    /// <summary>
    /// Operation methods on a document session that carry the document type as their single
    /// generic argument, whether it was written explicitly or inferred from the argument.
    /// </summary>
    private static readonly HashSet<string> DocumentOperationMethodNames = new()
        { "Store", "Insert", "Delete", "Update", "HardDelete" };

    /// <summary>
    /// Scans method bodies in an EventProjection class for calls to Store&lt;T&gt;, Insert&lt;T&gt;,
    /// Delete&lt;T&gt;, Update&lt;T&gt; on document operations to discover published document types.
    /// This handles the case where users override ApplyAsync directly and call operations
    /// methods with document types that are not otherwise registered.
    /// See https://github.com/JasperFx/marten/issues/4166
    /// </summary>
    /// <remarks>
    /// Resolution is SEMANTIC, deliberately. This used to match only <c>GenericNameSyntax</c>, which
    /// meant <c>ops.Store&lt;Doc&gt;(x)</c> registered the published type and the equally valid
    /// <c>ops.Store(x)</c> compiled and registered nothing at all — a silent trap, since the store
    /// then provisions that document's storage on demand and only the schema-ahead-of-time and
    /// known-document-type surfaces come up short. Binding the invocation gives the same answer for
    /// both spellings, because the type argument is on the symbol whether or not it was written down.
    /// The syntactic path stays as a fallback for code that does not bind (mid-edit, broken trees).
    /// </remarks>
    private static List<ITypeSymbol> DiscoverDocumentTypesFromMethodBodies(
        TypeDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol operationsType,
        SemanticModel? semanticModel,
        List<UnresolvedDocumentOperation>? unresolved = null)
    {
        var documentTypes = new List<ITypeSymbol>();
        var typeNames = new HashSet<string>();
        var seen = new HashSet<string>();

        foreach (var node in classDecl.DescendantNodes())
        {
            if (node is not InvocationExpressionSyntax invocation) continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;

            var simpleName = memberAccess.Name;
            if (!DocumentOperationMethodNames.Contains(simpleName.Identifier.ValueText)) continue;

            if (semanticModel != null &&
                TryResolveDocumentOperation(semanticModel, invocation, memberAccess, operationsType,
                    out var resolvedType, out var isOnOperations))
            {
                if (IsRegistrableDocumentType(resolvedType))
                {
                    if (seen.Add(resolvedType!.ToDisplayString()))
                    {
                        documentTypes.Add(resolvedType);
                    }
                }
                else if (isOnOperations && unresolved != null)
                {
                    // Bound to the operations session but the document type is not something we can
                    // register -- object, dynamic, an open type parameter. Nothing to emit, and the
                    // user cannot tell from the call site, so it is worth a diagnostic.
                    unresolved.Add(new UnresolvedDocumentOperation(
                        simpleName.Identifier.ValueText,
                        resolvedType?.ToDisplayString() ?? "?",
                        simpleName.GetLocation()));
                }

                continue;
            }

            // Fallback: unbindable code. Only the explicit spelling is recoverable from syntax alone.
            if (simpleName is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count == 1)
            {
                var typeName = ExtractTypeNameFromTypeSyntax(genericName.TypeArgumentList.Arguments[0]);
                if (typeName != null)
                {
                    typeNames.Add(typeName);
                }
            }
        }

        foreach (var tn in typeNames)
        {
            var resolved = FindTypeByName(tn, classSymbol);
            if (resolved != null && !IsFrameworkType(resolved) && seen.Add(resolved.ToDisplayString()))
            {
                documentTypes.Add(resolved);
            }
        }

        return documentTypes;
    }

    /// <summary>
    /// Bind a candidate document operation call and pull the document type off the method symbol.
    /// </summary>
    /// <param name="isOnOperations">
    /// True when the receiver really is the projection's operations session, which is what separates
    /// "we could not register this document type" from "this was somebody else's Store method".
    /// </param>
    private static bool TryResolveDocumentOperation(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        INamedTypeSymbol operationsType,
        out ITypeSymbol? documentType,
        out bool isOnOperations)
    {
        documentType = null;
        isOnOperations = false;

        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol
                     ?? semanticModel.GetSymbolInfo(invocation).CandidateSymbols.FirstOrDefault();

        if (symbol is not IMethodSymbol method) return false;

        var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
        isOnOperations = receiverType != null && IsOrImplements(receiverType, operationsType);

        // Extension methods hang the session off the first parameter rather than the receiver.
        if (!isOnOperations && method.IsExtensionMethod && method.ReducedFrom == null &&
            method.Parameters.Length > 0)
        {
            isOnOperations = IsOrImplements(method.Parameters[0].Type, operationsType);
        }

        if (!isOnOperations) return true;

        if (method.TypeArguments.Length == 1)
        {
            var candidate = method.TypeArguments[0];
            // Kept even when it is not registrable, so the diagnostic can name what was written.
            documentType = candidate;
        }

        return true;
    }

    /// <summary>
    /// Can this type be named in a <c>RegisterPublishedType(typeof(...))</c> call and mean something?
    /// </summary>
    /// <remarks>
    /// <c>SpecialType</c> is the check that matters and <see cref="IsFrameworkType"/> cannot stand in
    /// for it: <c>object</c> and <c>string</c> render through <c>ToDisplayString()</c> as their C#
    /// keywords, not as <c>System.Object</c> / <c>System.String</c>, so a name-prefix test lets them
    /// through and the projection ends up registering <c>object</c> as a published document type.
    /// </remarks>
    private static bool IsRegistrableDocumentType(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.TypeKind is TypeKind.TypeParameter or TypeKind.Dynamic or TypeKind.Error) return false;
        if (type.SpecialType != SpecialType.None) return false;

        return !IsFrameworkType(type);
    }

    private static bool IsOrImplements(ITypeSymbol type, INamedTypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(type, target)) return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, target)) return true;
        }

        var baseType = type.BaseType;
        while (baseType != null)
        {
            if (SymbolEqualityComparer.Default.Equals(baseType, target)) return true;
            baseType = baseType.BaseType;
        }

        return false;
    }

    private static List<ConventionalMethodInfo> DiscoverEventProjectionMethods(
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol operationsType)
    {
        var methods = new List<ConventionalMethodInfo>();
        var members = classSymbol.GetMembers();

        foreach (var member in members)
        {
            if (member is not IMethodSymbol method) continue;
            if (method.MethodKind != MethodKind.Ordinary) continue;
            if (method.Name is not ("Project" or "Create" or "Transform")) continue;
            if (HasJasperFxIgnoreAttribute(method)) continue;
            if (method.DeclaredAccessibility != Accessibility.Public) continue;

            var info = AnalyzeEventProjectionMethod(method, operationsType);
            if (info != null)
            {
                methods.Add(info);
            }
        }

        return methods;
    }

    private static ConventionalMethodInfo? AnalyzeEventProjectionMethod(
        IMethodSymbol method,
        INamedTypeSymbol operationsType)
    {
        var parameters = method.Parameters;
        if (parameters.Length == 0) return null;

        var info = new ConventionalMethodInfo
        {
            MethodName = method.Name,
            Symbol = method,
            IsStatic = method.IsStatic,
            IsOnAggregate = false
        };

        // Determine return type
        var returnType = method.ReturnType;
        info.IsAsync = IsAsyncReturnType(returnType);

        if (method.Name == "Project")
        {
            // Project must return void/Task/ValueTask
            if (info.IsAsync)
            {
                var unwrapped = UnwrapTaskType(returnType);
                if (unwrapped != null) return null; // Project must not return a value from Task<T>
                info.IsVoid = true; // Task/ValueTask with no result
            }
            else
            {
                if (returnType.SpecialType != SpecialType.System_Void) return null;
                info.IsVoid = true;
            }
        }
        else // Create or Transform
        {
            // Must return a non-void entity type (or Task<T>/ValueTask<T>)
            if (info.IsAsync)
            {
                var unwrapped = UnwrapTaskType(returnType);
                if (unwrapped == null) return null; // Must return Task<T> or ValueTask<T>
                info.EntityReturnType = unwrapped;
            }
            else
            {
                if (returnType.SpecialType == SpecialType.System_Void) return null;
                info.EntityReturnType = returnType;
            }
        }

        // Find event type and detect parameter types
        ITypeSymbol? eventType = null;

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.Type;

            if (IsIEventGenericWrapper(paramType, out var wrappedEventType))
            {
                info.UsesIEventWrapper = true;
                eventType = wrappedEventType;
                continue;
            }

            if (IsRawIEvent(paramType))
            {
                info.UsesRawIEvent = true;
                continue;
            }

            if (IsOperationsType(paramType, operationsType))
            {
                info.HasOperations = true;
                continue;
            }

            if (IsCancellationToken(paramType))
            {
                info.HasCancellationToken = true;
                continue;
            }

            // This must be the event type
            if (eventType == null)
            {
                eventType = paramType;
            }
        }

        if (eventType == null) return null;

        info.EventType = eventType;

        // Validate: Project methods must have TOperations parameter
        if (method.Name == "Project" && !info.HasOperations) return null;

        return info;
    }

    private static bool IsOperationsType(ITypeSymbol paramType, INamedTypeSymbol operationsType)
    {
        // Check direct match or if paramType is assignable to operationsType
        if (SymbolEqualityComparer.Default.Equals(paramType, operationsType)) return true;

        // Check if paramType is an interface that operationsType implements or extends
        // For Marten: IDocumentOperations is TOperations, but methods might use IDocumentOperations directly
        return paramType.ToDisplayString() == operationsType.ToDisplayString();
    }

    private static bool HasApplyAsyncOverride(INamedTypeSymbol classSymbol)
    {
        return classSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.IsOverride && m.Name == "ApplyAsync");
    }

    private static bool HasExplicitOverride(INamedTypeSymbol classSymbol)
    {
        return classSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.IsOverride && m.Name is "Evolve" or "EvolveAsync" or "DetermineAction" or "DetermineActionAsync");
    }

    /// <summary>
    /// Scan the projection's constructor(s) for the kept parameterless
    /// `DeleteEvent&lt;T&gt;()` registration API. Each unique type argument T
    /// becomes a switch arm in the emitted dispatcher that sets `snapshot =
    /// null` (i.e. delete the aggregate when an event of that type arrives).
    /// Mirrors the pre-#276 runtime behavior where DeleteEvent registrations
    /// were resolved reflectively. See #297.
    /// </summary>
    private static List<ITypeSymbol> DiscoverConstructorDeleteEventTypes(
        TypeDeclarationSyntax classDecl,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        var result = new List<ITypeSymbol>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.Body == null && ctor.ExpressionBody == null) continue;

            var nodes = ctor.Body is { } body
                ? body.DescendantNodes()
                : ctor.ExpressionBody!.DescendantNodes();

            foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
            {
                if (invocation.ArgumentList.Arguments.Count != 0) continue;

                var generic = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name as GenericNameSyntax,
                    GenericNameSyntax g => g,
                    _ => null
                };

                if (generic is null) continue;
                if (generic.Identifier.ValueText != "DeleteEvent") continue;
                if (generic.TypeArgumentList.Arguments.Count != 1) continue;

                var typeArgSyntax = generic.TypeArgumentList.Arguments[0];
                if (semanticModel.GetSymbolInfo(typeArgSyntax, ct).Symbol is not ITypeSymbol symbol) continue;
                if (seen.Add(symbol))
                {
                    result.Add(symbol);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when the aggregate type opts into identity-less boundary-aggregate evolver
    /// generation via [BoundaryAggregate] (JasperFx.Events.Aggregation). See #324.
    /// </summary>
    private static bool HasBoundaryAggregateAttribute(INamedTypeSymbol classSymbol)
    {
        return classSymbol.GetAttributes()
            .Any(a => a.AttributeClass?.Name is "BoundaryAggregateAttribute" or "BoundaryAggregate");
    }

    public static INamedTypeSymbol? InferIdentityType(INamedTypeSymbol classSymbol)
    {
        // 1. Check for an Id property OR a property/field tagged with
        // [Identity] (Marten.Schema.IdentityAttribute) — walk the inheritance
        // chain so aggregates that inherit Id from a user base class are still
        // recognized. Stop at framework bases and at System.Object. See #295.
        // The [Identity] attribute path covers user types like
        //   public record LoadTestInlineProjection { [Identity] public string StreamKey { get; init; } ... }
        // where the identity property isn't named "Id". Marten supports this
        // for document mapping in general and aggregates rely on it.
        for (INamedTypeSymbol? current = classSymbol;
             current is not null
             && current.SpecialType != SpecialType.System_Object
             && !IsFrameworkBase(current);
             current = current.BaseType)
        {
            // Prefer an [Identity]-attributed member (or field), since that's the
            // user's explicit override of the Id-by-convention rule.
            var attributedMember = current.GetMembers()
                .FirstOrDefault(m =>
                    (m is IPropertySymbol || m is IFieldSymbol)
                    && m.GetAttributes().Any(a => a.AttributeClass?.Name is "IdentityAttribute" or "Identity"));

            if (attributedMember is not null)
            {
                var memberType = attributedMember switch
                {
                    IPropertySymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    _ => null
                };

                if (memberType is INamedTypeSymbol attributedIdType)
                {
                    return UnwrapNullable(attributedIdType);
                }
            }

            var idProp = current.GetMembers()
                .OfType<IPropertySymbol>()
                .FirstOrDefault(p => p.Name == "Id");

            if (idProp?.Type is INamedTypeSymbol idType)
            {
                return UnwrapNullable(idType);
            }
        }

        // 2. Check for [AggregateIdentity(typeof(...))]
        var attr = classSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "AggregateIdentityAttribute" or "AggregateIdentity");

        if (attr?.ConstructorArguments.Length > 0 &&
            attr.ConstructorArguments[0].Value is INamedTypeSymbol attrIdType)
        {
            return attrIdType;
        }

        return null;
    }

    private static bool HasParameterlessConstructor(INamedTypeSymbol type)
    {
        // Always-true: the emitter knows how to instantiate every reference
        // type via `BuildAggregateConstructorExpression`, which picks between
        // `new T()`, `new T { ... = default! }` (required members with a
        // public parameterless ctor), or `RuntimeHelpers.GetUninitializedObject(typeof(T))`
        // (no public parameterless ctor). Keeping the property name + signature
        // unchanged so downstream gates that read it still compile; the value
        // now reflects "can the emitter produce an instance?" rather than
        // strictly "is there a public parameterless ctor?". See #297.
        return true;
    }

    /// <summary>
    /// Unwrap `Nullable&lt;T&gt;` so a `public PaymentId? Id { get; }` resolves to
    /// PaymentId (the runtime TId) instead of `Nullable&lt;PaymentId&gt;`. The
    /// runtime closes `SingleStreamProjection&lt;TDoc, TId&gt;` over the
    /// unwrapped struct type, so a generated evolver keyed on the nullable
    /// wrapper would never match. See #297.
    /// </summary>
    private static INamedTypeSymbol UnwrapNullable(INamedTypeSymbol type)
    {
        if (type.IsGenericType
            && type.ConstructedFrom.ToDisplayString() == "System.Nullable<T>"
            && type.TypeArguments.Length == 1
            && type.TypeArguments[0] is INamedTypeSymbol unwrapped)
        {
            return unwrapped;
        }
        return type;
    }

    private static bool HasRequiredMembers(INamedTypeSymbol type)
    {
        // Walk the inheritance chain — a required member on a base class still
        // forces the derived `new T()` to initialize it.
        for (INamedTypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IPropertySymbol prop when prop.IsRequired:
                    case IFieldSymbol field when field.IsRequired:
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if the type has a property marked with [NaturalKey] attribute
    /// </summary>
    private static bool HasNaturalKeyProperty(INamedTypeSymbol type)
    {
        return type.GetMembers()
            .OfType<IPropertySymbol>()
            .Any(p => p.GetAttributes()
                .Any(a => a.AttributeClass?.Name is "NaturalKeyAttribute" or "NaturalKey"));
    }

    /// <summary>
    /// Checks if the type has an explicitly declared parameterless constructor (not implicit).
    /// Used to avoid generating a duplicate constructor in partial classes.
    /// </summary>
    private static bool HasExplicitParameterlessConstructor(INamedTypeSymbol type)
    {
        return type.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 &&
            !c.IsImplicitlyDeclared);
    }

    /// <summary>
    /// Does this projection, or a user-written base class beneath ProjectionBase, already override
    /// <c>ProjectionBase.PublishedTypes()</c>? The generator defers to it rather than emitting a
    /// competing override. See marten#5192.
    /// </summary>
    private static bool HasPublishedTypesOverride(INamedTypeSymbol type)
    {
        var current = type;
        while (current != null && current.Name != "ProjectionBase")
        {
            foreach (var member in current.GetMembers("PublishedTypes"))
            {
                if (member is IMethodSymbol { IsOverride: true, Parameters.Length: 0 }) return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Checks if the type has a constructor that takes exactly one parameter that isn't
    /// a well-known framework type (i.e., it's likely an event parameter constructor
    /// used for Create). We skip generating evolvers for these types because the runtime
    /// handles them via expression compilation.
    /// </summary>
    private static bool HasEventParameterConstructor(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors;
        return constructors.Any(c =>
            c.Parameters.Length == 1 &&
            c.DeclaredAccessibility == Accessibility.Public &&
            !IsFrameworkType(c.Parameters[0].Type));
    }

    private static bool IsFrameworkType(ITypeSymbol type)
    {
        var name = type.ToDisplayString();
        return name.StartsWith("System.") || name.StartsWith("Microsoft.");
    }

    /// <summary>
    /// Analyze an aggregate type discovered via IRefersToAggregate attribute on a method parameter.
    /// Returns a CandidateInfo if the type is a valid self-aggregating type.
    /// </summary>
    public static CandidateInfo? AnalyzeRefersToAggregate(
        INamedTypeSymbol aggregateType,
        CancellationToken ct,
        INamedTypeSymbol? identityTypeHint = null)
    {
        if (IsProjectionBase(aggregateType)) return null;

        // Need a syntax reference to build CandidateInfo
        var syntaxRef = aggregateType.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef?.GetSyntax(ct) is not TypeDeclarationSyntax classDecl) return null;

        // Check for Evolve/EvolveAsync methods first
        var evolveMethod = FindEvolveMethod(aggregateType);
        if (evolveMethod != null)
        {
            var idType = InferIdentityType(aggregateType);
            if (idType == null) return null;

            var methodSyntax = evolveMethod.Symbol.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax(ct) as MethodDeclarationSyntax;
            if (methodSyntax != null)
            {
                ExtractEventTypesFromBody(methodSyntax, aggregateType, evolveMethod);
            }

            return new CandidateInfo
            {
                Mode = CandidateMode.SelfAggregatingEvolve,
                ClassSymbol = aggregateType,
                ClassSyntax = classDecl,
                AggregateType = aggregateType,
                IdentityType = idType,
                HasDefaultConstructor = HasParameterlessConstructor(aggregateType),
                EvolveMethod = evolveMethod
            };
        }

        // Try conventional methods
        var methods = DiscoverConventionalMethods(aggregateType, aggregateType, null);
        var eventCtors = DiscoverEventConstructors(aggregateType);
        if (methods.Count == 0 && eventCtors.Count == 0) return null;

        var inferredId = InferIdentityType(aggregateType) ?? identityTypeHint;
        if (inferredId == null) return null;

        return new CandidateInfo
        {
            Mode = CandidateMode.SelfAggregating,
            ClassSymbol = aggregateType,
            ClassSyntax = classDecl,
            AggregateType = aggregateType,
            IdentityType = inferredId,
            Methods = methods,
            EventConstructors = eventCtors,
            HasDefaultConstructor = HasParameterlessConstructor(aggregateType)
        };
    }

    /// <summary>
    /// Checks if a type symbol implements the IRefersToAggregate marker interface.
    /// </summary>
    public static bool ImplementsIRefersToAggregate(ITypeSymbol type)
    {
        const string markerInterface = "JasperFx.Events.Aggregation.IRefersToAggregate";

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ToDisplayString() == markerInterface) return true;
        }

        return false;
    }
}
