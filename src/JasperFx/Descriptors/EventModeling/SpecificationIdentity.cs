using System.Text.RegularExpressions;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The <c>{Feature}/{Scenario}</c> identity of a specification, derived from a spec class and a scenario
/// method the way Bobcat derives it for a projected (xUnit / TUnit) test (jasperfx#995).
/// </summary>
/// <remarks>
/// <para>
/// <b>A twin of Bobcat's <c>MarkerSpecNaming</c>, on purpose.</b> The identity is a join key: it is what
/// Bobcat publishes on <c>scenario_finished</c>, keys its declared steps by, and stamps on the
/// <see cref="SpecificationDescriptor"/>s it generates. A typed link written in an
/// <c>EventModelDefinition</c> has to produce the same string or it links to nothing, and JasperFx cannot
/// reference Bobcat — so the rule is restated here, and pinned by tests against Bobcat's own examples.
/// </para>
/// <para>
/// The feature title is the class's <c>[BobcatFeature("…")]</c> title when it has one, matched by
/// attribute name so no reference is needed. Otherwise it is the class name with one
/// <c>Specification</c>/<c>Specs</c>/<c>Spec</c>/<c>Fixture</c> suffix removed, read as a sentence. The
/// scenario title is the method name read as a sentence; there is no per-method override.
/// </para>
/// </remarks>
public static partial class SpecificationIdentity
{
    /// <summary>The separator between the feature and scenario titles.</summary>
    public const char Separator = '/';

    private const string FeatureAttributeName = "BobcatFeatureAttribute";

    private static readonly string[] titleSuffixes = ["Specification", "Specs", "Spec", "Fixture"];

    /// <summary>The identity of a scenario, from its two titles.</summary>
    public static string Of(string featureTitle, string scenarioTitle) => $"{featureTitle}{Separator}{scenarioTitle}";

    /// <summary>The identity of the scenario <paramref name="scenarioMethod"/> on <paramref name="specType"/>.</summary>
    /// <param name="specType">The specification class.</param>
    /// <param name="scenarioMethod">The scenario method's name — write it with <c>nameof</c>.</param>
    public static string For(Type specType, string scenarioMethod)
    {
        ArgumentNullException.ThrowIfNull(specType);
        if (string.IsNullOrWhiteSpace(scenarioMethod))
        {
            throw new ArgumentException("A specification link needs a scenario", nameof(scenarioMethod));
        }

        return Of(FeatureTitle(specType), ScenarioTitle(scenarioMethod));
    }

    /// <summary>The feature title of a specification class.</summary>
    public static string FeatureTitle(Type specType)
    {
        ArgumentNullException.ThrowIfNull(specType);

        // CustomAttributeData rather than an instance: the attribute is Bobcat's, read by name, and its
        // title is its one constructor argument. No member of an unknown type is reflected over.
        var title = specType.GetCustomAttributesData()
            .FirstOrDefault(x => x.AttributeType.Name == FeatureAttributeName)
            ?.ConstructorArguments.FirstOrDefault().Value as string;

        return FeatureTitle(specType.Name, title);
    }

    /// <summary>
    /// The feature title for a class name: <paramref name="attributeTitle"/> when there is one, otherwise
    /// the class name with one suffix removed, read as a sentence.
    /// </summary>
    public static string FeatureTitle(string className, string? attributeTitle)
    {
        if (attributeTitle != null) return attributeTitle;

        var name = className;
        foreach (var suffix in titleSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return Prettify(name);
    }

    /// <summary>The scenario title for a method name.</summary>
    public static string ScenarioTitle(string methodName) => Prettify(methodName);

    /// <summary>
    /// A C# identifier as a sentence: underscores become spaces, with empty segments dropped; failing
    /// that, a space before each word of a PascalCase name (<c>HTTPResponse</c> → "HTTP Response").
    /// </summary>
    public static string Prettify(string name)
        => name.Contains('_')
            ? string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries))
            : pascalCaseSplitter().Replace(name, " $1$2").Trim();

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex pascalCaseSplitter();
}
