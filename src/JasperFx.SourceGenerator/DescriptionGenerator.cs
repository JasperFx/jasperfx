using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JasperFx.SourceGenerator;

[Generator]
public sealed class DescriptionGenerator : IIncrementalGenerator
{
    private const string GenerateDescriptionAttributeName = "JasperFx.Descriptors.GenerateDescriptionAttribute";
    private const string IgnoreDescriptionAttributeName = "JasperFx.Descriptors.IgnoreDescriptionAttribute";
    private const string ChildDescriptionAttributeName = "JasperFx.Descriptors.ChildDescriptionAttribute";
    private const string IDescribeMyselfName = "JasperFx.Descriptors.IDescribeMyself";
    private const string ITaggedName = "JasperFx.Descriptors.ITagged";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            GenerateDescriptionAttributeName,
            predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
            transform: static (ctx, ct) => Analyze(ctx, ct))
            .Where(static info => info != null);

        context.RegisterSourceOutput(candidates, static (spc, info) => Execute(spc, info!));
    }

    private static DescriptionCandidate? Analyze(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var symbol = ctx.TargetSymbol as INamedTypeSymbol;
        if (symbol == null) return null;

        var candidate = new DescriptionCandidate
        {
            TypeSymbol = symbol,
            TypeSyntax = (TypeDeclarationSyntax)ctx.TargetNode,
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : symbol.ContainingNamespace.ToDisplayString(),
            TypeName = symbol.Name,
            IsPartial = ((TypeDeclarationSyntax)ctx.TargetNode).Modifiers.Any(SyntaxKind.PartialKeyword),
            ImplementsITagged = ImplementsInterface(symbol, ITaggedName),
        };

        // Gather properties
        foreach (var member in symbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic) continue;
            if (prop.IsIndexer) continue;

            // Check for [IgnoreDescription]
            if (HasAttribute(prop, IgnoreDescriptionAttributeName)) continue;

            // Check for [ChildDescription]
            if (HasAttribute(prop, ChildDescriptionAttributeName))
            {
                candidate.ChildProperties.Add(new PropertyInfo
                {
                    Name = prop.Name,
                    TypeFullName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsChild = true,
                    PropertyType = "None",
                });
                continue;
            }

            // Skip collection types (except string and string[])
            var propType = prop.Type;
            if (IsCollectionType(propType) && !IsStringArray(propType))
                continue;

            var info = new PropertyInfo
            {
                Name = prop.Name,
                TypeFullName = propType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsChild = false,
                PropertyType = ClassifyPropertyType(propType),
                IsNullable = propType.NullableAnnotation == NullableAnnotation.Annotated
                    || (propType is INamedTypeSymbol { IsGenericType: true } nts
                        && nts.ConstructedFrom.ToDisplayString() == "System.Nullable<T>"),
            };

            candidate.ValueProperties.Add(info);
        }

        return candidate;
    }

    private static string ClassifyPropertyType(ITypeSymbol type)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol { IsGenericType: true } nullable
            && nullable.ConstructedFrom.ToDisplayString() == "System.Nullable<T>")
        {
            type = nullable.TypeArguments[0];
        }

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Numeric types
        if (fullName is "int" or "long" or "short" or "byte" or "float" or "double" or "decimal"
            or "global::System.Int32" or "global::System.Int64" or "global::System.Int16"
            or "global::System.Byte" or "global::System.Single" or "global::System.Double"
            or "global::System.Decimal" or "uint" or "ulong" or "ushort")
        {
            return "Numeric";
        }

        if (type.TypeKind == TypeKind.Enum)
            return "Enum";

        if (fullName is "bool" or "global::System.Boolean")
            return "Boolean";

        if (fullName is "global::System.Uri")
            return "Uri";

        if (fullName is "global::System.TimeSpan")
            return "TimeSpan";

        if (fullName is "global::System.Type")
            return "Type";

        if (IsStringArray(type))
            return "StringArray";

        if (fullName is "global::System.Reflection.Assembly")
            return "Assembly";

        return "Text";
    }

    private static bool IsStringArray(ITypeSymbol type)
    {
        return type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String };
    }

    private static bool IsCollectionType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return false;
        if (type is IArrayTypeSymbol) return true;

        // Check for IEnumerable<T>
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>")
                return true;
        }

        // Also check if the type itself is IEnumerable
        if (type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>")
            return true;

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeFullName)
    {
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == attributeFullName);
    }

    private static bool ImplementsInterface(INamedTypeSymbol symbol, string interfaceFullName)
    {
        return symbol.AllInterfaces.Any(i => i.ToDisplayString() == interfaceFullName);
    }

    private static void Execute(SourceProductionContext context, DescriptionCandidate candidate)
    {
        if (!candidate.IsPartial)
        {
            // TODO: emit diagnostic that class must be partial
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using JasperFx.Descriptors;");
        sb.AppendLine();

        if (candidate.Namespace != null)
        {
            sb.AppendLine($"namespace {candidate.Namespace};");
            sb.AppendLine();
        }

        var keyword = candidate.TypeSyntax is RecordDeclarationSyntax ? "record" : "class";
        sb.AppendLine($"partial {keyword} {candidate.TypeName} : IDescribeMyself");
        sb.AppendLine("{");
        sb.AppendLine("    public OptionsDescription ToDescription()");
        sb.AppendLine("    {");
        sb.AppendLine($"        var description = new OptionsDescription");
        sb.AppendLine("        {");
        sb.AppendLine($"            Subject = \"{candidate.Namespace}.{candidate.TypeName}\"");
        sb.AppendLine("        };");
        sb.AppendLine();

        // Tags
        if (candidate.ImplementsITagged)
        {
            sb.AppendLine("        foreach (var tag in Tags)");
            sb.AppendLine("        {");
            sb.AppendLine("            description.AddTag(tag);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Value properties
        foreach (var prop in candidate.ValueProperties)
        {
            var subject = $"{candidate.Namespace}.{candidate.TypeName}.{prop.Name}";
            EmitValueProperty(sb, prop, subject);
        }

        // Child properties
        foreach (var child in candidate.ChildProperties)
        {
            sb.AppendLine($"        if ({child.Name} != null)");
            sb.AppendLine("        {");
            sb.AppendLine($"            description.Children[\"{child.Name}\"] = {child.Name} is IDescribeMyself __child_{child.Name}");
            sb.AppendLine($"                ? __child_{child.Name}.ToDescription()");
            sb.AppendLine($"                : new OptionsDescription({child.Name});");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        return description;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var hintName = candidate.Namespace != null
            ? $"{candidate.Namespace}.{candidate.TypeName}.Description.g.cs"
            : $"{candidate.TypeName}.Description.g.cs";

        context.AddSource(hintName, sb.ToString());
    }

    private static void EmitValueProperty(StringBuilder sb, PropertyInfo prop, string subject)
    {
        var escapedSubject = subject.Replace("\"", "\\\"");
        var escapedName = prop.Name.Replace("\"", "\\\"");

        switch (prop.PropertyType)
        {
            case "TimeSpan":
                if (prop.IsNullable)
                {
                    sb.AppendLine($"        if ({prop.Name} != null)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}.Value) {{ Type = PropertyType.TimeSpan, Value = {prop.Name}.Value.ToString() }});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.TimeSpan, Value = {prop.Name}.ToString() }});");
                }
                break;

            case "Boolean":
                if (prop.IsNullable)
                {
                    sb.AppendLine($"        if ({prop.Name} != null)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}.Value) {{ Type = PropertyType.Boolean, Value = {prop.Name}.Value.ToString() }});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.Boolean, Value = {prop.Name}.ToString() }});");
                }
                break;

            case "Numeric":
                if (prop.IsNullable)
                {
                    sb.AppendLine($"        if ({prop.Name} != null)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}.Value) {{ Type = PropertyType.Numeric, Value = {prop.Name}.Value.ToString() }});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.Numeric, Value = {prop.Name}.ToString() }});");
                }
                break;

            case "Enum":
                sb.AppendLine($"        description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", null!) {{ Type = PropertyType.Enum, Value = {prop.Name}.ToString() }});");
                break;

            case "Uri":
                sb.AppendLine($"        if ({prop.Name} != null)");
                sb.AppendLine("        {");
                sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.Uri, Value = {prop.Name}.ToString() }});");
                sb.AppendLine("        }");
                break;

            case "StringArray":
                sb.AppendLine($"        if ({prop.Name} != null)");
                sb.AppendLine("        {");
                sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.StringArray, Value = string.Join(\", \", {prop.Name}) }});");
                sb.AppendLine("        }");
                break;

            default: // Text
                if (prop.IsNullable)
                {
                    sb.AppendLine($"        if ({prop.Name} != null)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.Text, Value = {prop.Name}.ToString()! }});");
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        description.Properties.Add(new OptionsValue(\"{escapedSubject}\", \"{escapedName}\", {prop.Name}) {{ Type = PropertyType.Text, Value = {prop.Name}?.ToString()! }});");
                }
                break;
        }

        sb.AppendLine();
    }
}

internal class DescriptionCandidate
{
    public INamedTypeSymbol TypeSymbol { get; set; } = null!;
    public TypeDeclarationSyntax TypeSyntax { get; set; } = null!;
    public string? Namespace { get; set; }
    public string TypeName { get; set; } = "";
    public bool IsPartial { get; set; }
    public bool ImplementsITagged { get; set; }
    public List<PropertyInfo> ValueProperties { get; } = new();
    public List<PropertyInfo> ChildProperties { get; } = new();
}

internal class PropertyInfo
{
    public string Name { get; set; } = "";
    public string TypeFullName { get; set; } = "";
    public bool IsChild { get; set; }
    public string PropertyType { get; set; } = "Text";
    public bool IsNullable { get; set; }
}
