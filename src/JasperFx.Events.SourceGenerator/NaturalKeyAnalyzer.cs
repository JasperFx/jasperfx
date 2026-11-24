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

        // A problem that only involves inherited keys is reported on the base type that declares them, not
        // again on every type that derives from it. Base keys come first, so a clash between an inherited key
        // and one declared here lands on the one declared here.
        bool declaredHere(NaturalKeyInfo key) => SymbolEqualityComparer.Default.Equals(key.Property.ContainingType, type);

        var owned = keys.Where(x => x.Source == null).ToList();
        foreach (var extra in owned.Skip(1).Where(declaredHere))
        {
            diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MultipleOwnedNaturalKeys, extra.Location,
                type.Name, owned[0].Property.Name, extra.Property.Name));
        }

        var sourceDiagnostics = new List<Diagnostic>();

        // A mapped key whose type is wrong for its source (JFXEVT015) is the one mistake behind a clash with a
        // key of that type here too (JFXEVT011): declaring it with its source's key type, as JFXEVT015 says,
        // fixes both. So the clash is left unreported for it (jasperfx#1287).
        var mistyped = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var mapped in keys.Where(x => x.Source != null).Where(declaredHere))
        {
            var diagnostic = CheckSource(type, mapped);
            if (diagnostic == null) continue;

            sourceDiagnostics.Add(diagnostic);
            if (diagnostic.Id == DiagnosticDescriptors.NaturalKeySourceTypeMismatch.Id) mistyped.Add(mapped.Property);
        }

        foreach (var group in keys.GroupBy(x => x.KeyType, SymbolEqualityComparer.Default))
        {
            var sameType = group.Where(x => !mistyped.Contains(x.Property)).ToList();
            foreach (var extra in sameType.Skip(1).Where(declaredHere))
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.DuplicateNaturalKeyType, extra.Location,
                    type.Name, sameType[0].Property.Name, extra.Property.Name, extra.KeyType.ToDisplayString()));
            }
        }

        diagnostics.AddRange(sourceDiagnostics);

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

    /// <summary>
    /// JFXEVT018: an owned [NaturalKey] that nothing in this compilation sets. Off unless the project turns the
    /// diagnostic on, so nothing is scanned otherwise. Even when it is on, every doubt resolves to silence:
    /// a [NaturalKeySource] method or a NaturalKeyFor() call that could belong to the aggregate counts as
    /// setting the key, and any AddOrReplaceMapping() call, or a name that does not resolve, silences it.
    /// </summary>
    public static ImmutableArray<Diagnostic> AnalyzeUnsetKeys(Compilation compilation, CancellationToken token)
    {
        if (!IsTurnedOn(compilation, DiagnosticDescriptors.UnsetNaturalKey.Id, token))
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        // Every aggregate a NaturalKeyFor() call configures. A null entry is one the call does not pin to a
        // concrete type, such as a type parameter, and counts as any aggregate.
        var configured = new List<ITypeSymbol?>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            token.ThrowIfCancellationRequested();

            SemanticModel? model = null;
            foreach (var name in tree.GetRoot(token).DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var text = name.Identifier.ValueText;
                if (text != "NaturalKeyFor" && text != "AddOrReplaceMapping") continue;

                model ??= compilation.GetSemanticModel(tree);
                var info = model.GetSymbolInfo(name, token);
                var method = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;

                // Can't tell what this is, so it might register anything
                if (method == null) return ImmutableArray<Diagnostic>.Empty;

                if (text == "AddOrReplaceMapping")
                {
                    // A mapping added straight to a NaturalKeyDefinition can't be traced to an aggregate
                    if (method.ContainingType?.Name == "NaturalKeyDefinition") return ImmutableArray<Diagnostic>.Empty;
                    continue;
                }

                if (method.ContainingType is { Name: "JasperFxAggregationProjectionBase", Arity: 4 } projectionBase
                    && projectionBase.ContainingNamespace.ToDisplayString() == AttributeNamespace)
                {
                    configured.Add(projectionBase.TypeArguments[0] as INamedTypeSymbol);
                }
            }
        }

        var sourceTypes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, token)
            .OfType<INamedTypeSymbol>()
            .ToList();

        // Types with a [NaturalKeySource] method of their own or inherited
        var withSources = sourceTypes.Where(HasNaturalKeySource).ToList();

        var diagnostics = new List<Diagnostic>();
        foreach (var type in sourceTypes)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                // Discovery only reads public instance properties
                if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public) continue;
                if (KeyOf(property) is not { Source: null } key) continue;

                if (configured.Any(x => x == null || AreRelated(x, type))) continue;

                if (withSources.Any(x => AreRelated(x, type) || AggregateMightBe(x, type))) continue;

                if (HasNaturalKeySource(type)) continue;

                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.UnsetNaturalKey, key.Location,
                    type.Name, property.Name));
            }
        }

        return diagnostics.ToImmutableArray();
    }

    /// <summary>
    /// Whether the project turned on a diagnostic that is off by default, in its compilation options, in a
    /// global config, or in an .editorconfig section for any of its files.
    /// </summary>
    private static bool IsTurnedOn(Compilation compilation, string id, CancellationToken token)
    {
        static bool on(ReportDiagnostic report) => report is not (ReportDiagnostic.Default or ReportDiagnostic.Suppress);

        if (compilation.Options.SpecificDiagnosticOptions.TryGetValue(id, out var specific) && on(specific)) return true;

        var provider = compilation.Options.SyntaxTreeOptionsProvider;
        if (provider == null) return false;

        if (provider.TryGetGlobalDiagnosticValue(id, token, out var global) && on(global)) return true;

        return compilation.SyntaxTrees.Any(tree => provider.TryGetDiagnosticValue(tree, id, token, out var value) && on(value));
    }

    /// <summary>
    /// A [NaturalKeySource] method on the type or anywhere up its base types, at any accessibility, so that
    /// one the runtime refuses for another reason does not draw this diagnostic too.
    /// </summary>
    private static bool HasNaturalKeySource(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                foreach (var attribute in method.GetAttributes())
                {
                    for (var attributeClass = attribute.AttributeClass; attributeClass != null; attributeClass = attributeClass.BaseType)
                    {
                        if (attributeClass.Name == "NaturalKeySourceAttribute") return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>One type derives from the other, or either is not a concrete type (a type parameter, say).</summary>
    private static bool AreRelated(ITypeSymbol candidate, INamedTypeSymbol aggregate)
    {
        if (candidate is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error) return true;

        return InheritsFrom(named, aggregate) || InheritsFrom(aggregate, named);
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol ancestor)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, ancestor.OriginalDefinition)) return true;
        }

        return false;
    }

    /// <summary>
    /// The type is an aggregate projection whose aggregate is, or might be, the given one: the aggregate is
    /// related to it, or is a type parameter.
    /// </summary>
    private static bool AggregateMightBe(INamedTypeSymbol type, INamedTypeSymbol aggregate)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current is { Name: "JasperFxAggregationProjectionBase", Arity: 4 }
                && current.ContainingNamespace.ToDisplayString() == AttributeNamespace)
            {
                return AreRelated(current.TypeArguments[0], aggregate);
            }
        }

        return false;
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

            // NaturalKey = typeof(...) is set after the constructor runs, so it wins over a type argument
            var named = attribute.NamedArguments.FirstOrDefault(x => x.Key == "NaturalKey");
            if (named.Value.Value is ITypeSymbol explicitKey) return explicitKey;

            return current.Arity == 1 ? current.TypeArguments[0] : null;
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

    /// <summary>
    /// The natural keys of a type, its base types' included, as runtime discovery sees them: public instance
    /// properties, inherited ones too. Base types come first, and a property overridden further down counts
    /// once, as its most-derived declaration that carries the attribute.
    /// </summary>
    private static List<NaturalKeyInfo> NaturalKeysOf(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            chain.Add(current);
        }

        chain.Reverse();

        var keys = new List<NaturalKeyInfo>();
        foreach (var declaring in chain)
        {
            var inherited = !SymbolEqualityComparer.Default.Equals(declaring, type);
            foreach (var property in declaring.GetMembers().OfType<IPropertySymbol>())
            {
                if (inherited && (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public)) continue;

                if (KeyOf(property) is not { } key) continue;

                var overridden = keys.FindIndex(x => x.Property.Name == property.Name);
                if (overridden >= 0)
                {
                    keys[overridden] = key;
                }
                else
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    private static NaturalKeyInfo? KeyOf(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (!IsNaturalKeyAttribute(attribute.AttributeClass, out var isMappedGeneric)) continue;

            ITypeSymbol? source;
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

            return new NaturalKeyInfo(property, source as INamedTypeSymbol, location);
        }

        return null;
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
