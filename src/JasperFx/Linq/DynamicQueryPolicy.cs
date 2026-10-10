using System.Linq.Expressions;

namespace JasperFx.Linq;

/// <summary>
/// What <see cref="DynamicQuery" /> will accept: size caps, and the shape rules a store adds for expressions
/// its LINQ provider would translate <em>wrongly</em> rather than refuse.
/// </summary>
/// <remarks>
/// <para>
/// The built-in allow-list always runs and cannot be turned off here — it is what stands between a text box
/// and in-process evaluation (see <see cref="DynamicQuery" />). A policy only tightens it.
/// </para>
/// <para>
/// <b>Why stores need <see cref="ShapeRules" />.</b> Most shapes a provider cannot translate fail honestly
/// with an exception. A few translate into SQL that runs and returns the wrong rows — the jasperfx#869 spike
/// found <c>PlacedAt.Year</c> rendered as a JSON path <c>$.placedAt.year</c> on two stores. A store that
/// knows of such a shape adds a rule so the text is refused before it reaches the provider, because a
/// silent wrong answer to an operator's filter is worse than no answer.
/// </para>
/// </remarks>
public sealed record DynamicQueryPolicy
{
    /// <summary>The caps below and no store rules.</summary>
    public static DynamicQueryPolicy Default { get; } = new();

    /// <summary>
    /// Longest accepted <see cref="DynamicQueryText.Where" /> or <see cref="DynamicQueryText.OrderBy" />, in
    /// characters. Default 2,000.
    /// </summary>
    public int MaxTextLength { get; init; } = 2_000;

    /// <summary>
    /// Most expression nodes accepted in one parsed clause. Default 500 — a generous bound for anything
    /// typed by hand, and a cheap one against pathological input.
    /// </summary>
    public int MaxNodes { get; init; } = 500;

    /// <summary>Most <see cref="DynamicQueryText.Arguments" /> accepted. Default 64.</summary>
    public int MaxArguments { get; init; } = 64;

    /// <summary>Store-specific refusals, applied after the built-in allow-list.</summary>
    public IReadOnlyList<IDynamicQueryShapeRule> ShapeRules { get; init; } = [];

    /// <summary>A copy of this policy with <paramref name="rules" /> appended.</summary>
    public DynamicQueryPolicy WithRules(params IDynamicQueryShapeRule[] rules)
        => this with { ShapeRules = [.. ShapeRules, .. rules] };
}

/// <summary>
/// A store-specific refusal of an expression shape — see <see cref="DynamicQueryPolicy.ShapeRules" />.
/// </summary>
public interface IDynamicQueryShapeRule
{
    /// <summary>
    /// Inspect one node of a parsed clause. Return null to allow it, or the reason to refuse it — written for
    /// the person who typed the query, so say what to do instead.
    /// </summary>
    string? Refuse(Expression node);
}

/// <summary>Ready-made <see cref="IDynamicQueryShapeRule" />s for the shapes stores most often mistranslate.</summary>
public static class DynamicQueryShapeRules
{
    /// <summary>A rule from a delegate.</summary>
    public static IDynamicQueryShapeRule For(Func<Expression, string?> refuse) => new DelegateRule(refuse);

    /// <summary>
    /// Refuse reading a member <em>of</em> a value of type <typeparamref name="T" /> (or <c>T?</c>) — e.g.
    /// <c>PlacedAt.Year</c> for <c>DateTime</c>.
    /// </summary>
    public static IDynamicQueryShapeRule NoMemberAccessOn<T>(string reason)
        => For(node => node is MemberExpression { Expression: { } owner }
                       && (owner.Type == typeof(T) || Nullable.GetUnderlyingType(owner.Type) == typeof(T))
            ? reason
            : null);

    /// <summary>
    /// Refuse a <c>Count</c> or <c>Length</c> property read off a collection (strings excepted) — e.g.
    /// <c>Items.Count</c>.
    /// </summary>
    public static IDynamicQueryShapeRule NoCollectionSizeProperty(string reason)
        => For(node => node is MemberExpression { Expression: { } owner, Member.Name: "Count" or "Length" }
                       && owner.Type != typeof(string)
                       && typeof(System.Collections.IEnumerable).IsAssignableFrom(owner.Type)
            ? reason
            : null);

    /// <summary>Refuse calls to any method named <paramref name="methodName" /> on <paramref name="declaringType" />.</summary>
    public static IDynamicQueryShapeRule NoMethod(Type declaringType, string methodName, string reason)
        => For(node => node is MethodCallExpression call && call.Method.DeclaringType == declaringType &&
                       call.Method.Name == methodName
            ? reason
            : null);

    /// <summary>
    /// Refuse <c>&lt;&gt;</c>, or <c>not</c> over a comparison or string method, on a member that can be null —
    /// unless the text says what a null should do (<c>Notes != "x" or Notes = null</c>, or
    /// <c>Notes != null and Notes != "x"</c>).
    /// </summary>
    /// <remarks>
    /// SQL compares with three-valued logic: <c>NULL &lt;&gt; 'x'</c> is unknown, so a SQL-backed store leaves out
    /// every row whose member is null, where the text read as C# keeps them. Measured on Marten, Polecat and Fisher
    /// alike (jasperfx#869). Whether a member can be null comes from <see cref="Nullable{T}" /> and nullable
    /// reference annotations; an unannotated reference member counts as nullable. Every SQL-backed store should
    /// add this rule; an in-memory store should not, because it has no null problem to protect against.
    /// </remarks>
    public static IDynamicQueryShapeRule SqlNullSemantics() => new SqlNullSemanticsRule();

    private sealed class DelegateRule(Func<Expression, string?> refuse) : IDynamicQueryShapeRule
    {
        public string? Refuse(Expression node) => refuse(node);
    }
}
