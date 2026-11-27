#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.SourceGenerator;

/// <summary>
/// Discovers <c>JasperFx.Events.EventModeling.EventModelDefinition</c> subclasses at compile time and
/// emits a per-assembly <c>JasperFx.Generated.DiscoveredEventModels.DefinitionTypes</c> manifest, so
/// <c>AddDiscoveredEventModels(assembly)</c> registers an app's definitions without listing each one and
/// without an <c>Assembly.ExportedTypes</c> scan (jasperfx#993).
/// </summary>
/// <remarks>
/// <para>
/// A definition qualifies when it is concrete, public all the way out (so nested inside public types is
/// fine), not generic, and has a public constructor. The constructor may take parameters: definitions are
/// built through <c>ActivatorUtilities</c>, so constructor-injected services are allowed. The manifest
/// roots each definition's public constructors with <c>[DynamicDependency]</c> so a trimmed app keeps
/// them.
/// </para>
/// <para>
/// Emitted whenever the compilation can see <c>EventModelDefinition</c> — an empty manifest included —
/// because an empty manifest is the evidence that the generator ran: without it, the runtime cannot tell
/// "no definitions here" from "never generated", and would fall back to the reflective scan.
/// </para>
/// <para>
/// <b>Field shapes</b> (jasperfx#1299). For every type a definition names through a type-first verb in its
/// <c>Configure</c> method — <c>Command&lt;T&gt;</c>, <c>Emits&lt;T&gt;</c>, <c>View&lt;T&gt;</c>, <c>Against&lt;T&gt;</c>
/// and the rest — the manifest also carries the type's fields as <c>TypeShapeDescriptor</c>s, under
/// <c>DiscoveredEventModels.Shapes</c> keyed by definition type. Read off the type's public properties and
/// fields with <c>typeof</c>, never reflection, so the console path stays AOT-clean. Flags come from
/// attributes only: <c>[Identity]</c> / <c>[Key]</c> (and the <c>Id</c> convention) for identity,
/// <c>[Generated]</c> / <c>[DatabaseGenerated]</c>, <c>[Pii]</c>, <c>[Required]</c> or <c>required</c> against
/// nullability for optional. Nothing is inferred from a method body.
/// </para>
/// </remarks>
[Generator]
public sealed class EventModelDiscoveryGenerator : IIncrementalGenerator
{
    private const string DefinitionMetadataName = "JasperFx.Events.EventModeling.EventModelDefinition";
    private const string ShapeMetadataName = "JasperFx.Events.EventModeling.TypeShapeDescriptor";
    private const string ModelingNamespace = "JasperFx.Events.EventModeling";

    /// <summary>
    /// The type-first verbs whose type argument names a modelled element with fields worth describing. Handler
    /// types, projections, tags, specifications and identity types are deliberately left out: their members are
    /// services and plumbing, not information the model carries.
    /// </summary>
    private static readonly HashSet<string> ShapedVerbs = new(StringComparer.Ordinal)
    {
        "Command", "Against", "UsesAggregate", "StartsStream", "DeciderModel", "DcbModel", "Decides",
        "Emits", "Publishes", "On", "From", "Reads", "Produces", "View", "Query", "Automation",
        "Aggregate", "ForAggregate", "Shape", "TriggeredBy", "Describe",
    };

    /// <summary>
    /// One qualifying definition: its fully qualified name, and the C# for its shapes. A struct with value
    /// equality so the incremental pipeline caches it; a record would need IsExternalInit on netstandard2.0.
    /// </summary>
    private readonly struct Candidate : IEquatable<Candidate>
    {
        public Candidate(string typeName, string shapesCode)
        {
            TypeName = typeName;
            ShapesCode = shapesCode;
        }

        public string TypeName { get; }
        public string ShapesCode { get; }

        public bool Equals(Candidate other)
            => string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
               && string.Equals(ShapesCode, other.ShapesCode, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is Candidate other && Equals(other);

        public override int GetHashCode() => unchecked(TypeName.GetHashCode() * 397 ^ ShapesCode.GetHashCode());
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var definitions = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax cds
                                               && cds.BaseList != null
                                               && !cds.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                               && !cds.Modifiers.Any(SyntaxKind.StaticKeyword),
                transform: static (ctx, ct) => AnalyzeClass(ctx, ct))
            .Where(static candidate => candidate.HasValue)
            .Collect();

        var available = context.CompilationProvider.Select(static (compilation, _) =>
            (definitions: compilation.GetTypeByMetadataName(DefinitionMetadataName) != null,
                shapes: compilation.GetTypeByMetadataName(ShapeMetadataName) != null));

        context.RegisterSourceOutput(definitions.Combine(available),
            static (spc, pair) => Execute(spc, pair.Left, pair.Right.definitions, pair.Right.shapes));
    }

    private static Candidate? AnalyzeClass(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct) is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (symbol.IsAbstract || symbol.IsStatic) return null;
        if (!DerivesFromDefinition(symbol)) return null;
        if (!IsPublicAndClosed(symbol)) return null;

        var hasPublicConstructor = symbol.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public);
        if (!hasPublicConstructor) return null;

        var shapes = ctx.SemanticModel.Compilation.GetTypeByMetadataName(ShapeMetadataName) is null
            ? string.Empty
            : ShapesCodeFor((ClassDeclarationSyntax)ctx.Node, ctx.SemanticModel, ct);

        return new Candidate(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), shapes);
    }

    private static bool DerivesFromDefinition(INamedTypeSymbol symbol)
    {
        for (var current = symbol.BaseType; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == DefinitionMetadataName) return true;
        }

        return false;
    }

    /// <summary>
    /// Public at every level of nesting and generic at none — the only shape a registration from
    /// another assembly can name, and one <c>typeof</c> can close.
    /// </summary>
    private static bool IsPublicAndClosed(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current != null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
            if (current.IsGenericType) return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ field shapes (jasperfx#1299)

    /// <summary>
    /// The shapes of every type the definition's <c>Configure</c> names through a shaped verb, as the C# of a
    /// <c>TypeShapeDescriptor[]</c> initializer — or an empty string when it names none.
    /// </summary>
    private static string ShapesCodeFor(ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken ct)
    {
        var types = new List<INamedTypeSymbol>();

        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText != "Configure") continue;

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();

                var generic = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
                    GenericNameSyntax g => g,
                    _ => null,
                };
                if (generic is null || !ShapedVerbs.Contains(generic.Identifier.ValueText)) continue;

                if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target
                    || target.ContainingType?.ContainingNamespace?.ToDisplayString() != ModelingNamespace)
                {
                    continue;
                }

                // Only the first type argument names an element: By<TCommand, TTag> names the command first, and
                // Tag<TTag, TInner> is not a shaped verb at all.
                var argument = generic.TypeArgumentList.Arguments.FirstOrDefault();
                if (argument is null) continue;

                if (model.GetTypeInfo(argument, ct).Type is not INamedTypeSymbol type) continue;
                if (!IsShapeable(type)) continue;
                if (types.Any(x => SymbolEqualityComparer.Default.Equals(x, type))) continue;

                types.Add(type);
            }
        }

        if (types.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var type in types.OrderBy(x => x.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal))
        {
            AppendShape(sb, type);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A class, struct or record the manifest can name with <c>typeof</c>: closed, not a primitive, and
    /// accessible from the assembly's own code (public or internal at every level of nesting).
    /// </summary>
    private static bool IsShapeable(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        if (type.SpecialType != SpecialType.None) return false;
        if (type.IsUnboundGenericType || type.IsAnonymousType) return false;
        if (ContainsTypeParameter(type)) return false;

        return IsAccessibleHere(type);
    }

    private static bool IsAccessibleHere(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) return false;
        }

        return true;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    private static void AppendShape(StringBuilder sb, INamedTypeSymbol type)
    {
        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        sb.AppendLine("            new global::JasperFx.Events.EventModeling.TypeShapeDescriptor(");
        sb.AppendLine($"                global::JasperFx.Descriptors.TypeDescriptor.For(typeof({typeName})),");
        sb.AppendLine("                new global::JasperFx.Events.EventModeling.FieldDescriptor[]");
        sb.AppendLine("                {");

        foreach (var member in FieldsOf(type))
        {
            AppendField(sb, type, member);
        }

        sb.AppendLine("                })");
        sb.AppendLine("            { Provenance = global::JasperFx.Events.EventModeling.EventModelProvenance.Derived },");
    }

    /// <summary>
    /// The public instance properties (with a getter, not indexers) and public instance fields of the type
    /// and its base types, base first, in declaration order — the members a record's positional parameters
    /// become, and what reflection would see.
    /// </summary>
    private static IEnumerable<ISymbol> FieldsOf(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current != null && current.SpecialType == SpecialType.None; current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in chain)
        {
            foreach (var member in level.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public) continue;

                switch (member)
                {
                    case IPropertySymbol { IsIndexer: false, GetMethod: not null } property when seen.Add(property.Name):
                        yield return property;
                        break;
                    case IFieldSymbol { IsConst: false } field when seen.Add(field.Name):
                        yield return field;
                        break;
                }
            }
        }
    }

    private static void AppendField(StringBuilder sb, INamedTypeSymbol owner, ISymbol member)
    {
        var memberType = member switch
        {
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            _ => null,
        };
        if (memberType is null) return;

        var optional = memberType.NullableAnnotation == NullableAnnotation.Annotated;
        var type = memberType;

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            optional = true;
            type = nullable.TypeArguments[0];
        }

        var many = false;
        if (type is IArrayTypeSymbol array)
        {
            many = true;
            type = array.ElementType;
        }
        else if (type.SpecialType != SpecialType.System_String && ElementTypeOf(type) is { } element)
        {
            many = true;
            type = element;
        }

        // The attributes of a record's positional parameter land on the parameter unless targeted at the
        // property, so both places are read.
        var attributes = member.GetAttributes().ToList();
        var parameter = owner.InstanceConstructors
            .SelectMany(x => x.Parameters)
            .FirstOrDefault(x => string.Equals(x.Name, member.Name, StringComparison.Ordinal));
        if (parameter is not null) attributes.AddRange(parameter.GetAttributes());

        bool has(params string[] names) => attributes.Any(a => names.Contains(a.AttributeClass?.ToDisplayString()));

        var identity = has("JasperFx.IdentityAttribute", "System.ComponentModel.DataAnnotations.KeyAttribute")
                       || string.Equals(member.Name, "Id", StringComparison.OrdinalIgnoreCase);
        var generated = has("JasperFx.GeneratedAttribute", "System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute");
        var pii = has("JasperFx.PiiAttribute");
        var required = has("System.ComponentModel.DataAnnotations.RequiredAttribute")
                       || (member is IPropertySymbol { IsRequired: true })
                       || (member is IFieldSymbol { IsRequired: true });
        if (required) optional = false;

        // A property's own <summary>, else the <param> the record's doc comment wrote for its positional parameter.
        var description = SummaryOf(member) ?? (parameter is null ? null : ParamDocOf(owner, parameter.Name));

        sb.Append("                    new global::JasperFx.Events.EventModeling.FieldDescriptor(");
        sb.Append(Literal(member.Name));
        sb.Append(", ");
        sb.Append(TypeDescriptorCode(type));
        sb.Append(')');

        var initializers = new List<string>();
        if (many) initializers.Add("Cardinality = global::JasperFx.Events.EventModeling.FieldCardinality.Many");
        if (optional) initializers.Add("Optional = true");
        if (identity) initializers.Add("Identity = true");
        if (generated) initializers.Add("Generated = true");
        if (pii) initializers.Add("Pii = true");
        if (description is not null) initializers.Add($"Description = {Literal(description)}");

        if (initializers.Count > 0)
        {
            sb.Append(" { ").Append(string.Join(", ", initializers)).Append(" }");
        }

        sb.AppendLine(",");
    }

    /// <summary>The <c>T</c> of an <c>IEnumerable&lt;T&gt;</c> the type is or implements, else null.</summary>
    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return null;

        foreach (var candidate in new[] { named }.Concat(named.AllInterfaces))
        {
            if (candidate.IsGenericType && candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// <c>TypeDescriptor.For(typeof(X))</c> when <c>typeof</c> can name the type from this assembly, else a
    /// descriptor by name — the same marker a declaration leaves, so the merge still matches it by name.
    /// </summary>
    private static string TypeDescriptorCode(ITypeSymbol type)
    {
        var nameable = !ContainsTypeParameter(type)
                       && type is not IDynamicTypeSymbol
                       && type is not IPointerTypeSymbol
                       && (type is not INamedTypeSymbol named || (!named.IsAnonymousType && IsAccessibleHere(named)));

        if (nameable)
        {
            var display = type.WithNullableAnnotation(NullableAnnotation.None)
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"global::JasperFx.Descriptors.TypeDescriptor.For(typeof({display}))";
        }

        var simple = type.Name;
        var full = type.ToDisplayString();
        return $"new global::JasperFx.Descriptors.TypeDescriptor({Literal(simple)}, {Literal(full)}, \"\")";
    }

    /// <summary>The <c>&lt;summary&gt;</c> of a member's XML doc comment, whitespace collapsed, or null.</summary>
    private static string? SummaryOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(summary)) return null;

            return string.Join(" ", summary!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The <c>&lt;param name="…"&gt;</c> of the type's XML doc comment for a positional parameter, or null.</summary>
    private static string? ParamDocOf(INamedTypeSymbol owner, string parameterName)
    {
        var xml = owner.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var param = XDocument.Parse(xml).Descendants("param")
                .FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), parameterName, StringComparison.Ordinal))?.Value;
            if (string.IsNullOrWhiteSpace(param)) return null;

            return string.Join(" ", param!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Literal(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    // ------------------------------------------------------------------ emission

    private static void Execute(SourceProductionContext context, ImmutableArray<Candidate?> definitions,
        bool available, bool shapesAvailable)
    {
        if (!available) return;

        var candidates = definitions
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .GroupBy(x => x.TypeName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.TypeName, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Diagnostics.CodeAnalysis;");
        sb.AppendLine();
        sb.AppendLine("namespace JasperFx.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Source-generated manifest of the EventModelDefinition subclasses in this assembly. Read by");
        sb.AppendLine("/// AddDiscoveredEventModels(assembly) in place of a reflective type scan. See jasperfx#993.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class DiscoveredEventModels");
        sb.AppendLine("{");

        // The definitions are built through ActivatorUtilities, so their public constructors must
        // survive trimming. DynamicDependency is not allowed on a property, hence the backing field.
        foreach (var candidate in candidates)
        {
            sb.AppendLine($"    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof({candidate.TypeName}))]");
        }

        sb.AppendLine("    private static readonly Type[] _definitionTypes =");
        sb.AppendLine("    {");
        foreach (var candidate in candidates)
        {
            sb.AppendLine($"        typeof({candidate.TypeName}),");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public static IReadOnlyList<Type> DefinitionTypes => _definitionTypes;");

        if (shapesAvailable)
        {
            // jasperfx#1299. The fields of the types each definition names, read off the code at compile time
            // so the console path never reflects over a record. Keyed by definition so the shapes land in the
            // model that definition contributes to.
            const string shapeList = "IReadOnlyList<global::JasperFx.Events.EventModeling.TypeShapeDescriptor>";

            sb.AppendLine();
            sb.AppendLine($"    private static readonly Dictionary<Type, {shapeList}> _shapes = new Dictionary<Type, {shapeList}>");
            sb.AppendLine("    {");
            foreach (var candidate in candidates.Where(x => x.ShapesCode.Length > 0))
            {
                sb.AppendLine("        {");
                sb.AppendLine($"            typeof({candidate.TypeName}),");
                sb.AppendLine("            new global::JasperFx.Events.EventModeling.TypeShapeDescriptor[]");
                sb.AppendLine("            {");
                sb.Append(candidate.ShapesCode);
                sb.AppendLine("            }");
                sb.AppendLine("        },");
            }

            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>The field shapes of the types each definition names, derived from the code (jasperfx#1299).</summary>");
            sb.AppendLine($"    public static IReadOnlyDictionary<Type, {shapeList}> Shapes => _shapes;");
        }

        sb.AppendLine("}");

        context.AddSource("DiscoveredEventModels.g.cs", sb.ToString());
    }
}
