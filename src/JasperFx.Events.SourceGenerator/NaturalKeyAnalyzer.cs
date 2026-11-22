using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.Events.SourceGenerator;

/// <summary>
/// Build-time validation of [NaturalKey] / [MappedNaturalKey&lt;T&gt;] (JFXEVT010-015). Independent of
/// the evolver pipelines: any type carrying a natural key attribute is checked, whether or not the
/// generator emits anything for it. A source projection is resolved through its symbol, so a source
/// in a referenced assembly is checked too.
/// </summary>
internal static class NaturalKeyAnalyzer
{
    private const string AttributeNamespace = "JasperFx.Events.Aggregation";

    /// <summary>
    /// Every [NaturalKey] subclass is named "...NaturalKey...", so this cheaply narrows the syntax walk
    /// to type declarations that might carry one. Semantic checks follow in <see cref="Analyze" />.
    /// </summary>
    public static bool IsPossibleCandidate(SyntaxNode node)
    {
        if (node is not TypeDeclarationSyntax type) return false;

        foreach (var attribute in type.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (attribute.Name.ToString().Contains("NaturalKey")) return true;
        }

        return false;
    }

    public static ImmutableArray<Diagnostic> Analyze(GeneratorSyntaxContext context, CancellationToken token)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, token) is not INamedTypeSymbol type)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        var keys = NaturalKeysOf(type);
        if (keys.Count == 0) return ImmutableArray<Diagnostic>.Empty;

        var diagnostics = new List<Diagnostic>();

        var owned = keys.Where(x => x.Source == null).ToList();
        foreach (var extra in owned.Skip(1))
        {
            diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MultipleOwnedNaturalKeys, extra.Location,
                type.Name, owned[0].Property.Name, extra.Property.Name));
        }

        foreach (var group in keys.GroupBy(x => x.KeyType, SymbolEqualityComparer.Default))
        {
            var sameType = group.ToList();
            foreach (var extra in sameType.Skip(1))
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.DuplicateNaturalKeyType, extra.Location,
                    type.Name, sameType[0].Property.Name, extra.Property.Name, extra.KeyType.ToDisplayString()));
            }
        }

        foreach (var mapped in keys.Where(x => x.Source != null))
        {
            var diagnostic = CheckSource(type, mapped);
            if (diagnostic != null) diagnostics.Add(diagnostic);
        }

        return diagnostics.ToImmutableArray();
    }

    private static Diagnostic? CheckSource(INamedTypeSymbol type, NaturalKeyInfo mapped)
    {
        var source = mapped.Source!;

        // A bad typeof() is already CS0246; don't pile a second error on it.
        if (source.TypeKind == TypeKind.Error) return null;

        var sourceAggregate = AggregateOf(source);

        if (SymbolEqualityComparer.Default.Equals(sourceAggregate ?? source, type))
        {
            return Diagnostic.Create(DiagnosticDescriptors.NaturalKeySourceIsSelf, mapped.Location,
                type.Name, mapped.Property.Name);
        }

        // Either an aggregate projection (whose aggregate owns the key) or a self-aggregating type.
        var sourceKeys = NaturalKeysOf(sourceAggregate ?? source);

        var sameTypeKey = sourceKeys.FirstOrDefault(x =>
            SymbolEqualityComparer.Default.Equals(x.KeyType, mapped.KeyType));
        if (sameTypeKey is { Source: not null })
        {
            return Diagnostic.Create(DiagnosticDescriptors.NaturalKeySourceIsMapped, mapped.Location,
                type.Name, mapped.Property.Name, source.Name, sameTypeKey.Property.Name);
        }

        var sourceOwned = sourceKeys.FirstOrDefault(x => x.Source == null);
        if (sourceOwned == null)
        {
            var reason = sourceAggregate == null && sourceKeys.Count == 0
                ? "is neither an aggregate projection nor an aggregate with a [NaturalKey] property"
                : "does not own a natural key";

            return Diagnostic.Create(DiagnosticDescriptors.NaturalKeySourceHasNoKey, mapped.Location,
                type.Name, mapped.Property.Name, source.Name, reason);
        }

        if (!SymbolEqualityComparer.Default.Equals(sourceOwned.KeyType, mapped.KeyType))
        {
            return Diagnostic.Create(DiagnosticDescriptors.NaturalKeySourceTypeMismatch, mapped.Location,
                type.Name, mapped.Property.Name, mapped.Property.Type.ToDisplayString(),
                source.Name, sourceOwned.Property.Name, sourceOwned.Property.Type.ToDisplayString());
        }

        return null;
    }

    private static readonly string[] ModelAttributeNames =
        ["WriteAggregateAttribute", "ReadAggregateAttribute", "WriteModelAttribute", "ReadModelAttribute"];

    /// <summary>
    /// A parameter carrying an attribute that may be one of Wolverine's model attributes. Semantic checks
    /// follow in <see cref="AnalyzeModelParameter" />.
    /// </summary>
    public static bool IsPossibleModelParameter(SyntaxNode node)
    {
        if (node is not ParameterSyntax { AttributeLists.Count: > 0 } parameter) return false;

        foreach (var attribute in parameter.AttributeLists.SelectMany(x => x.Attributes))
        {
            var name = attribute.Name.ToString();
            if (name.Contains("Aggregate") || name.Contains("Model")) return true;
        }

        return false;
    }

    /// <summary>
    /// JFXEVT016-017: the natural key type a Wolverine model attribute names, through its type argument or
    /// <c>NaturalKey = typeof(...)</c>, has to be one the model declares with [NaturalKey] or
    /// [MappedNaturalKey&lt;T&gt;], and never <c>Nullable&lt;T&gt;</c>.
    /// </summary>
    public static ImmutableArray<Diagnostic> AnalyzeModelParameter(GeneratorSyntaxContext context,
        CancellationToken token)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, token) is not IParameterSymbol parameter)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        var diagnostics = new List<Diagnostic>();

        foreach (var attribute in parameter.GetAttributes())
        {
            var keyType = NamedNaturalKeyType(attribute);
            if (keyType == null || keyType.TypeKind == TypeKind.Error) continue;

            var location = attribute.ApplicationSyntaxReference?.GetSyntax(token).GetLocation()
                           ?? parameter.Locations.FirstOrDefault()
                           ?? Location.None;

            if (keyType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.ModelNaturalKeyIsNullable, location,
                    parameter.Name, keyType.ToDisplayString(), nullable.TypeArguments[0].ToDisplayString()));
                continue;
            }

            if (ModelOf(parameter.Type) is not { } model || model.TypeKind == TypeKind.Error) continue;

            var keys = NaturalKeysOf(model);
            if (keys.Any(x => SymbolEqualityComparer.Default.Equals(x.KeyType, keyType))) continue;

            var known = keys.Count == 0
                ? "has no natural keys"
                : "only has natural keys of type " +
                  string.Join(", ", keys.Select(x => $"'{x.KeyType.ToDisplayString()}'"));

            diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.ModelNaturalKeyNotFound, location,
                parameter.Name, keyType.ToDisplayString(), model.Name, known));
        }

        return diagnostics.ToImmutableArray();
    }

    /// <summary>
    /// The key type a Wolverine model attribute names: <c>[WriteAggregate&lt;TKey&gt;]</c> and the like, or
    /// <c>NaturalKey = typeof(TKey)</c> on any of them. Null for anything else.
    /// </summary>
    private static ITypeSymbol? NamedNaturalKeyType(AttributeData attribute)
    {
        // Subclasses count too, as Wolverine.Marten's [WriteAggregate] derives from core's [WriteModel]
        for (var current = attribute.AttributeClass; current != null; current = current.BaseType)
        {
            if (!ModelAttributeNames.Contains(current.Name) ||
                current.ContainingNamespace?.ToDisplayString().StartsWith("Wolverine") != true)
            {
                continue;
            }

            if (current.Arity == 1) return current.TypeArguments[0];

            var named = attribute.NamedArguments.FirstOrDefault(x => x.Key == "NaturalKey");
            return named.Value.Value as ITypeSymbol;
        }

        return null;
    }

    /// <summary>
    /// The model a parameter loads: the parameter's type, or <c>T</c> for <c>IEventStream&lt;T&gt;</c>.
    /// </summary>
    private static INamedTypeSymbol? ModelOf(ITypeSymbol parameterType)
    {
        if (parameterType is INamedTypeSymbol { Name: "IEventStream", Arity: 1 } stream)
        {
            return stream.TypeArguments[0] as INamedTypeSymbol;
        }

        return parameterType as INamedTypeSymbol;
    }

    /// <summary>
    /// The aggregate (TDoc) of a projection class, found by walking its base types to
    /// JasperFxAggregationProjectionBase&lt;TDoc, TId, TOperations, TQuerySession&gt;. Null when the type is
    /// not an aggregate projection, e.g. a self-aggregating snapshot.
    /// </summary>
    private static INamedTypeSymbol? AggregateOf(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current is { Name: "JasperFxAggregationProjectionBase", Arity: 4 }
                && current.ContainingNamespace.ToDisplayString() == AttributeNamespace)
            {
                return current.TypeArguments[0] as INamedTypeSymbol;
            }
        }

        return null;
    }

    private static List<NaturalKeyInfo> NaturalKeysOf(INamedTypeSymbol type)
    {
        var keys = new List<NaturalKeyInfo>();

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            foreach (var attribute in property.GetAttributes())
            {
                if (!IsNaturalKeyAttribute(attribute.AttributeClass, out var isMappedGeneric)) continue;

                ITypeSymbol? source = null;
                if (isMappedGeneric)
                {
                    source = attribute.AttributeClass!.TypeArguments[0];
                }
                else
                {
                    var named = attribute.NamedArguments.FirstOrDefault(x => x.Key == "SourceProjection");
                    source = named.Value.Value as ITypeSymbol;
                }

                var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                               ?? property.Locations.FirstOrDefault()
                               ?? Location.None;

                keys.Add(new NaturalKeyInfo(property, source as INamedTypeSymbol, location));
                break;
            }
        }

        return keys;
    }

    private static bool IsNaturalKeyAttribute(INamedTypeSymbol? attributeClass, out bool isMappedGeneric)
    {
        isMappedGeneric = false;
        if (attributeClass == null || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
        {
            return false;
        }

        if (attributeClass.Name == "NaturalKeyAttribute") return true;

        if (attributeClass is { Name: "MappedNaturalKeyAttribute", Arity: 1 })
        {
            isMappedGeneric = true;
            return true;
        }

        return false;
    }

    private sealed class NaturalKeyInfo
    {
        public NaturalKeyInfo(IPropertySymbol property, INamedTypeSymbol? source, Location location)
        {
            Property = property;
            Source = source;
            Location = location;
        }

        public IPropertySymbol Property { get; }

        /// <summary>
        /// The key type the property stands for: <c>T</c> for a <c>Nullable&lt;T&gt;</c> property, which is the
        /// same key as a property of type <c>T</c>.
        /// </summary>
        public ITypeSymbol KeyType => Property.Type is INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
        } nullable
            ? nullable.TypeArguments[0]
            : Property.Type;

        /// <summary>The source projection for a mapped key; null for an owned key.</summary>
        public INamedTypeSymbol? Source { get; }

        public Location Location { get; }
    }
}
