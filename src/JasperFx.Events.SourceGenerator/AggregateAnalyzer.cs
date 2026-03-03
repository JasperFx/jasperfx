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
    EventProjection
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

internal sealed class CandidateInfo
{
    public CandidateMode Mode { get; set; }
    public INamedTypeSymbol ClassSymbol { get; set; } = null!;
    public ClassDeclarationSyntax ClassSyntax { get; set; } = null!;
    public bool IsPartial { get; set; }
    public INamedTypeSymbol? AggregateType { get; set; } // TDoc
    public INamedTypeSymbol? IdentityType { get; set; } // TId
    public INamedTypeSymbol? QuerySessionType { get; set; } // TQuerySession
    public List<ConventionalMethodInfo> Methods { get; set; } = new();
    public bool HasShouldDelete => Methods.Any(m => m.MethodName == "ShouldDelete");
    public bool HasAnyAsync => Methods.Any(m => m.IsAsync);
    public bool HasCreate => Methods.Any(m => m.MethodName == "Create");
    public bool HasDefaultConstructor { get; set; }
    public bool HasExistingParameterlessConstructor { get; set; } // On the projection class itself
    // EventProjection-specific
    public INamedTypeSymbol? OperationsType { get; set; } // TOperations from JasperFxEventProjectionBase<TOperations, TQuerySession>
}

internal static class AggregateAnalyzer
{
    private const string AggregationProjectionBaseFullName =
        "JasperFx.Events.Aggregation.JasperFxAggregationProjectionBase";

    private const string EventProjectionBaseFullName =
        "JasperFx.Events.Projections.JasperFxEventProjectionBase";

    private const string ProjectionBaseFullName = "JasperFx.Events.Projections.ProjectionBase";

    private static readonly string[] LambdaMethodNames =
        { "ProjectEvent", "CreateEvent", "DeleteEvent" };

    private static readonly string[] EventProjectionLambdaMethodNames =
        { "Project<", "ProjectAsync<" };

    public static CandidateInfo? Analyze(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);
        if (classSymbol == null) return null;

        // Determine if this is a projection subclass or self-aggregating
        var projBaseInfo = FindAggregationProjectionBase(classSymbol);

        if (projBaseInfo != null)
        {
            return AnalyzeProjectionSubclass(classDecl, classSymbol, projBaseInfo.Value, ct);
        }

        // Check if this is an EventProjection subclass
        var eventProjBaseInfo = FindEventProjectionBase(classSymbol);
        if (eventProjBaseInfo != null)
        {
            return AnalyzeEventProjectionSubclass(classDecl, classSymbol, eventProjBaseInfo.Value, ct);
        }

        // Not a projection subclass - check if self-aggregating
        if (!IsProjectionBase(classSymbol))
        {
            return AnalyzeSelfAggregating(classDecl, classSymbol, ct);
        }

        return null;
    }

    private static CandidateInfo? AnalyzeProjectionSubclass(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        (INamedTypeSymbol docType, INamedTypeSymbol idType, INamedTypeSymbol querySessionType) baseInfo,
        CancellationToken ct)
    {
        var isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);

        // Check if it already overrides Evolve/EvolveAsync/DetermineAction/DetermineActionAsync
        if (HasExplicitOverride(classSymbol))
            return null;

        // Check if constructor has lambda registrations
        if (HasLambdaRegistrations(classDecl))
        {
            return new CandidateInfo
            {
                Mode = CandidateMode.None,
                ClassSymbol = classSymbol,
                ClassSyntax = classDecl
            };
        }

        var methods = DiscoverConventionalMethods(classSymbol, baseInfo.docType, classSymbol);

        if (methods.Count == 0) return null;

        return new CandidateInfo
        {
            Mode = isPartial ? CandidateMode.PartialProjection : CandidateMode.None,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = isPartial,
            AggregateType = baseInfo.docType,
            IdentityType = baseInfo.idType,
            QuerySessionType = baseInfo.querySessionType,
            Methods = methods,
            HasDefaultConstructor = HasParameterlessConstructor(baseInfo.docType),
            HasExistingParameterlessConstructor = HasExplicitParameterlessConstructor(classSymbol)
        };
    }

    private static CandidateInfo? AnalyzeSelfAggregating(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        CancellationToken ct)
    {
        var methods = DiscoverConventionalMethods(classSymbol, classSymbol, null);
        if (methods.Count == 0) return null;

        // Skip types that use constructor-based creation (we don't generate for these)
        if (HasEventParameterConstructor(classSymbol))
            return null;

        // Infer TId from Id property or AggregateIdentityAttribute
        var idType = InferIdentityType(classSymbol);
        if (idType == null) return null; // Will emit diagnostic elsewhere

        return new CandidateInfo
        {
            Mode = CandidateMode.SelfAggregating,
            ClassSymbol = classSymbol,
            ClassSyntax = classDecl,
            IsPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
            AggregateType = classSymbol,
            IdentityType = idType,
            Methods = methods,
            HasDefaultConstructor = HasParameterlessConstructor(classSymbol)
        };
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
        var members = searchType.GetMembers();
        foreach (var member in members)
        {
            if (member is not IMethodSymbol method) continue;
            if (method.MethodKind != MethodKind.Ordinary) continue;
            if (method.Name is not ("Apply" or "Create" or "ShouldDelete")) continue;
            if (HasJasperFxIgnoreAttribute(method)) continue;
            // Skip non-public methods - we can't call them from generated code
            if (method.DeclaredAccessibility != Accessibility.Public) continue;

            var info = AnalyzeMethod(method, aggregateType, isOnAggregate);
            if (info != null)
            {
                results.Add(info);
            }
        }
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

            if (SymbolEqualityComparer.Default.Equals(paramType, aggregateType) && !isOnAggregate)
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
                if (current.TypeArguments.Length >= 4)
                {
                    return (
                        (INamedTypeSymbol)current.TypeArguments[0],
                        (INamedTypeSymbol)current.TypeArguments[1],
                        (INamedTypeSymbol)current.TypeArguments[3]
                    );
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
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        (INamedTypeSymbol operationsType, INamedTypeSymbol querySessionType) baseInfo,
        CancellationToken ct)
    {
        var isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);

        // Check if it already overrides ApplyAsync
        if (HasApplyAsyncOverride(classSymbol))
            return null;

        // Check if constructor has lambda registrations (Project<T> / ProjectAsync<T>)
        if (HasEventProjectionLambdaRegistrations(classDecl))
            return null;

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
            HasExistingParameterlessConstructor = HasExplicitParameterlessConstructor(classSymbol)
        };
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

    private static bool HasEventProjectionLambdaRegistrations(ClassDeclarationSyntax classDecl)
    {
        var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
        foreach (var ctor in constructors)
        {
            if (ctor.Body == null) continue;
            var text = ctor.Body.ToFullString();
            foreach (var name in EventProjectionLambdaMethodNames)
            {
                if (text.Contains(name))
                    return true;
            }
        }

        return false;
    }

    private static bool HasExplicitOverride(INamedTypeSymbol classSymbol)
    {
        return classSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.IsOverride && m.Name is "Evolve" or "EvolveAsync" or "DetermineAction" or "DetermineActionAsync");
    }

    private static bool HasLambdaRegistrations(ClassDeclarationSyntax classDecl)
    {
        var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
        foreach (var ctor in constructors)
        {
            if (ctor.Body == null) continue;
            var text = ctor.Body.ToFullString();
            foreach (var name in LambdaMethodNames)
            {
                if (text.Contains(name))
                    return true;
            }
        }

        return false;
    }

    public static INamedTypeSymbol? InferIdentityType(INamedTypeSymbol classSymbol)
    {
        // 1. Check for Id property
        var idProp = classSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name == "Id");

        if (idProp?.Type is INamedTypeSymbol idType)
            return idType;

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
        // If no explicit constructors, there's an implicit default
        var constructors = type.InstanceConstructors;
        if (constructors.Length == 0) return true;

        return constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
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
}
