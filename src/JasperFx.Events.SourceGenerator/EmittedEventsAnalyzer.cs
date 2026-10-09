using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace JasperFx.Events.SourceGenerator;

/// <summary>
/// One handler method's inferred events, reduced to strings so the incremental pipeline can compare
/// it by value. Holding symbols here would defeat caching: a symbol from one compilation never
/// equals the same symbol from the next, so every edit would look like a change to every method.
/// </summary>
internal sealed class EmittedEventsInfo : IEquatable<EmittedEventsInfo>
{
    public EmittedEventsInfo(string handlerType, string methodName, ImmutableArray<string> eventTypes)
    {
        HandlerType = handlerType;
        MethodName = methodName;
        EventTypes = eventTypes;
    }

    /// <summary>Fully qualified, <c>global::</c>-rooted.</summary>
    public string HandlerType { get; }

    public string MethodName { get; }

    /// <summary>Fully qualified, <c>global::</c>-rooted, deduplicated, in order of first appearance.</summary>
    public ImmutableArray<string> EventTypes { get; }

    public bool Equals(EmittedEventsInfo? other)
    {
        if (other is null) return false;
        return HandlerType == other.HandlerType
               && MethodName == other.MethodName
               && EventTypes.SequenceEqual(other.EventTypes);
    }

    public override bool Equals(object? obj) => Equals(obj as EmittedEventsInfo);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = HandlerType.GetHashCode() * 397 ^ MethodName.GetHashCode();
            foreach (var type in EventTypes) hash = hash * 31 + type.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// Infers the events a handler method appends from its body, keyed off JasperFx.Events marker types
/// rather than Wolverine's attribute or type names. See jasperfx#990.
/// </summary>
internal static class EmittedEventsAnalyzer
{
    private const string EventStreamMetadataName = "JasperFx.Events.IEventStream<T>";
    private const string CarriesEventsMetadataName = "JasperFx.Events.ICarriesEvents";
    public const string ManifestAttributeMetadataName = "JasperFx.Events.EmittedEventsAttribute";

    /// <summary>
    /// Instance members of an <see cref="CarriesEventsMetadataName" /> type that add to it. Matched by
    /// name because the marker says nothing about which members mutate: <c>Contains(object)</c> has
    /// the same event-shaped parameter as <c>Add(object)</c>.
    /// </summary>
    private static readonly HashSet<string> AddingMethodNames = new()
    {
        "Add", "AddRange", "Insert", "InsertRange", "Append", "AppendOne", "AppendMany"
    };

    /// <summary>
    /// Syntax predicate. Cheap and name-blind: a method with a body that either takes a parameter or
    /// returns something — the only shapes that can satisfy any candidate rule. Everything else is
    /// decided semantically in <see cref="Analyze" />.
    /// </summary>
    public static bool IsPossibleCandidate(SyntaxNode node)
    {
        if (node is not MethodDeclarationSyntax method) return false;
        if (method.Body is null && method.ExpressionBody is null) return false;
        if (method.ExplicitInterfaceSpecifier != null) return false;

        if (method.ParameterList.Parameters.Count > 0) return true;

        return !(method.ReturnType is PredefinedTypeSyntax predefined
                 && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword));
    }

    public static EmittedEventsInfo? Analyze(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var syntax = (MethodDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(syntax, ct) is not IMethodSymbol method) return null;

        // The manifest names the handler with typeof() at assembly scope, so the type has to be
        // nameable from there — and the issue scopes this to public types in any case.
        if (!IsPublicNonGeneric(method.ContainingType)) return null;
        if (method.IsGenericMethod) return null;

        if (!IsCandidate(method)) return null;

        var body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody;
        if (body is null) return null;

        var operation = ctx.SemanticModel.GetOperation(body, ct);
        if (operation is null) return null;

        var collector = new EventCollector(ctx.SemanticModel, ct);
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            ct.ThrowIfCancellationRequested();
            if (descendant is IInvocationOperation invocation) collector.Visit(invocation);
            else if (descendant is IObjectCreationOperation creation) collector.Visit(creation);
        }

        if (collector.Events.Count == 0) return null;

        return new EmittedEventsInfo(
            method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            method.Name,
            collector.Events.ToImmutableArray());
    }

    private static bool IsCandidate(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (IsEventStream(parameter.Type)) return true;

            foreach (var attribute in parameter.GetAttributes())
            {
                if (attribute.AttributeClass != null
                    && AggregateAnalyzer.ImplementsIRefersToAggregate(attribute.AttributeClass))
                {
                    return true;
                }
            }
        }

        var returnType = UnwrapTask(method.ReturnType);
        if (returnType is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            return tuple.TupleElements.Any(e => CarriesEvents(UnwrapTask(e.Type)));
        }

        return CarriesEvents(returnType);
    }

    private static ITypeSymbol UnwrapTask(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            var definition = named.ConstructedFrom.ToDisplayString();
            if (definition is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                return named.TypeArguments[0];
            }
        }

        return type;
    }

    /// <summary>
    /// JasperFx.Events' own <c>IEventStream&lt;T&gt;</c>, or a store's interface that inherits it —
    /// matched by symbol, so no marker is needed for this case.
    /// </summary>
    private static bool IsEventStream(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (IsEventStreamDefinition(named)) return true;
        return named.AllInterfaces.Any(IsEventStreamDefinition);
    }

    private static bool IsEventStreamDefinition(INamedTypeSymbol type)
    {
        return type.IsGenericType && type.ConstructedFrom.ToDisplayString() == EventStreamMetadataName;
    }

    private static bool CarriesEvents(ITypeSymbol? type)
    {
        if (type is null || type is ITypeParameterSymbol) return false;
        if (type.ToDisplayString() == CarriesEventsMetadataName) return true;
        return type.AllInterfaces.Any(i => i.ToDisplayString() == CarriesEventsMetadataName);
    }

    /// <summary>
    /// Public all the way out and closed: the only shape a <c>typeof</c> at assembly scope can name
    /// that is also within the issue's "public type" scope.
    /// </summary>
    private static bool IsPublicNonGeneric(INamedTypeSymbol? type)
    {
        for (var current = type; current != null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
            if (current.IsGenericType) return false;
        }

        return type != null;
    }

    private sealed class EventCollector
    {
        private readonly SemanticModel _model;
        private readonly CancellationToken _ct;
        private readonly HashSet<string> _seen = new();

        public EventCollector(SemanticModel model, CancellationToken ct)
        {
            _model = model;
            _ct = ct;
        }

        public List<string> Events { get; } = new();

        public void Visit(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            var receiver = invocation.Instance?.Type;

            var appendsToStream = method.Name is "AppendOne" or "AppendMany" && IsEventStream(receiver);
            var addsToCarrier = AddingMethodNames.Contains(method.Name) && CarriesEvents(receiver);
            var buildsCarrier = CarriesEvents(method.ReturnType);

            if (appendsToStream || addsToCarrier || buildsCarrier)
            {
                CollectArguments(invocation.Arguments);
            }
        }

        public void Visit(IObjectCreationOperation creation)
        {
            // The collection-initializer half (`new Events { new A() }`) needs nothing here: each
            // element is an implicit Add() invocation on the new instance, which Visit(invocation)
            // already sees as an add to an ICarriesEvents receiver.
            if (CarriesEvents(creation.Type))
            {
                CollectArguments(creation.Arguments);
            }
        }

        private void CollectArguments(ImmutableArray<IArgumentOperation> arguments)
        {
            foreach (var argument in arguments)
            {
                var parameter = argument.Parameter;
                if (parameter is null) continue;

                if (argument.ArgumentKind == ArgumentKind.ParamArray)
                {
                    // Expanded `params object[]`: the compiler built the array, one element per event.
                    if (IsObject(ElementTypeOf(parameter.Type)) && argument.Value is IArrayCreationOperation array)
                    {
                        CollectElements(array);
                    }

                    continue;
                }

                if (IsObject(parameter.Type))
                {
                    CollectValue(argument.Value);
                }
                else if (IsObject(ElementTypeOf(parameter.Type)))
                {
                    CollectSequence(argument.Value);
                }

                // Anything else — a Guid or string stream id, an index — is not an event slot.
            }
        }

        /// <summary>A value passed where one event is expected.</summary>
        private void CollectValue(IOperation value)
        {
            value = UnwrapImplicitConversions(value);

            if (value is IConditionalOperation conditional)
            {
                if (conditional.WhenTrue != null) CollectValue(conditional.WhenTrue);
                if (conditional.WhenFalse != null) CollectValue(conditional.WhenFalse);
                return;
            }

            if (value is ICoalesceOperation coalesce)
            {
                CollectValue(coalesce.Value);
                CollectValue(coalesce.WhenNull);
                return;
            }

            Add(value.Type);
        }

        /// <summary>A value passed where a sequence of events is expected.</summary>
        private void CollectSequence(IOperation value)
        {
            value = UnwrapImplicitConversions(value);

            if (value is IArrayCreationOperation array)
            {
                CollectElements(array);
                return;
            }

            // Collection expressions (`[new A(), new B()]`) have no IOperation shape in the Roslyn
            // this analyzer targets, so read their elements from syntax.
            if (value.Syntax is CollectionExpressionSyntax collection)
            {
                foreach (var element in collection.Elements.OfType<ExpressionElementSyntax>())
                {
                    var elementOperation = _model.GetOperation(element.Expression, _ct);
                    if (elementOperation != null) CollectValue(elementOperation);
                }

                return;
            }

            // A typed sequence (List<A>, A[]) still names its events through its element type.
            Add(ElementTypeOf(value.Type));
        }

        private void CollectElements(IArrayCreationOperation array)
        {
            if (array.Initializer is null) return;
            foreach (var element in array.Initializer.ElementValues)
            {
                CollectValue(element);
            }
        }

        private void Add(ITypeSymbol? type)
        {
            if (!IsNameableEventType(type)) return;

            var name = type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (_seen.Add(name)) Events.Add(name);
        }
    }

    private static IOperation UnwrapImplicitConversions(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static bool IsObject(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_Object;

    /// <summary>The element type of an array or of the first <c>IEnumerable&lt;T&gt;</c> the type is or implements.</summary>
    private static ITypeSymbol? ElementTypeOf(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type is not INamedTypeSymbol named) return null;

        static bool IsEnumerableOfT(INamedTypeSymbol t) =>
            t.IsGenericType && t.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

        if (IsEnumerableOfT(named)) return named.TypeArguments[0];

        var enumerable = named.AllInterfaces.FirstOrDefault(IsEnumerableOfT);
        return enumerable?.TypeArguments[0];
    }

    /// <summary>
    /// Whether the static type says anything (<c>object</c> does not) and can be named by a
    /// <c>typeof</c> at assembly scope: no type parameters, nothing private or protected on the way out.
    /// </summary>
    private static bool IsNameableEventType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.TypeKind == TypeKind.Error || named.IsAnonymousType) return false;
        if (named.SpecialType == SpecialType.System_Object) return false;

        return IsNameableFromAssemblyScope(named);
    }

    private static bool IsNameableFromAssemblyScope(INamedTypeSymbol type)
    {
        foreach (var argument in type.TypeArguments)
        {
            if (argument is not INamedTypeSymbol namedArgument) return false;
            if (!IsNameableFromAssemblyScope(namedArgument)) return false;
        }

        for (var current = type; current != null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    public static string EmitManifest(ImmutableArray<EmittedEventsInfo?> infos)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// Emitted by JasperFx.Events.SourceGenerator: the events each handler method was inferred");
        builder.AppendLine("// to append, read from its body. A lower bound — an event typed `object` is left out.");
        builder.AppendLine("// See jasperfx#990.");

        // Overloads share a name, and the manifest keys on the name, so merge them into one entry.
        var merged = new Dictionary<(string, string), List<string>>();
        var order = new List<(string, string)>();
        foreach (var info in infos)
        {
            if (info is null) continue;
            var key = (info.HandlerType, info.MethodName);
            if (!merged.TryGetValue(key, out var events))
            {
                merged[key] = events = new List<string>();
                order.Add(key);
            }

            foreach (var type in info.EventTypes)
            {
                if (!events.Contains(type)) events.Add(type);
            }
        }

        foreach (var key in order)
        {
            var (handler, methodName) = key;
            var typeofs = string.Join(", ", merged[key].Select(t => $"typeof({t})"));
            builder.AppendLine(
                $"[assembly: global::JasperFx.Events.EmittedEvents(typeof({handler}), \"{methodName}\", {typeofs})]");
        }

        return builder.ToString();
    }
}
