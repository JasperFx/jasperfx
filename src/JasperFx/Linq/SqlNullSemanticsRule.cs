using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace JasperFx.Linq;

// jasperfx#869 — lifted from Fisher (fisher-869 CriteriaShapeRules), where it was found: SQL's three-valued
// logic makes a null-blind inequality silently drop rows on EVERY SQL-backed store. CritterWatch's cross-store
// parity test then measured the same 0-of-3 answer on Marten and Polecat, so it belongs here, once.
// Exposed through DynamicQueryShapeRules.SqlNullSemantics().

/// <summary>
///     <c>&lt;&gt;</c>, or <c>not</c> over a comparison, on a member that can be null — unless the text
///     says what a null should do.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it is wrong.</b> SQL compares with three-valued logic: <c>NULL &lt;&gt; 'x'</c> is
///         unknown, so a row whose member is null is left out — where C#, and therefore the text as the
///         console user reads it, keeps it (<c>null != "x"</c> is true). <c>Note &lt;&gt; "fragile"</c>
///         over seven documents with no note returned none of them. <c>not (Note = "x")</c> and
///         <c>not Note.Contains("x")</c> fail the same way, because <c>NOT unknown</c> is unknown.
///     </para>
///     <para>
///         <b>When it is allowed.</b> When the member cannot be null — a non-nullable value type, or a
///         reference type the document declares non-nullable (nullable reference annotations, read with
///         <see cref="NullabilityInfoContext" />; an unannotated member counts as nullable) — or when the
///         text settles the null case itself, which is what the refusal asks for:
///         <c>Note &lt;&gt; "x" or Note = null</c> keeps those rows on both readings, and
///         <c>Note != null and Note &lt;&gt; "x"</c> drops them on both. A guard on a longer path covers
///         its prefixes (<c>ShipTo.City = null</c> is true when <c>ShipTo</c> is null, in Fisher's
///         translation and in null-propagating C# alike), so a nested member is settled by guarding the
///         path that is compared.
///     </para>
///     <para>
///         <b>How the guard is seen.</b> A shape rule is shown one node at a time with no ancestors, but
///         the allow-list walks top-down, so every guard is visited before what it guards. A guard
///         therefore marks the uses it covers in a <see cref="ConditionalWeakTable{TKey,TValue}" /> keyed
///         by the use's node — fresh nodes per parse, so concurrent parses never share an entry — and the
///         use, when its turn comes, is refused only for a nullable link no guard marked.
///     </para>
///     <para>
///         Comparisons inside a collection predicate (<c>Items.Any(Sku &lt;&gt; "x")</c>) are held to the
///         same rule against the element's members; the collection operator itself is an
///         <c>EXISTS</c>, which is never unknown, so <c>not Items.Any(...)</c> is fine.
///     </para>
/// </remarks>
internal sealed class SqlNullSemanticsRule : IDynamicQueryShapeRule
{
    // Both keyed by the node of one comparison, method call or member read — fresh per parse.
    private readonly ConditionalWeakTable<Expression, HashSet<string>> _covered = new();
    private readonly ConditionalWeakTable<Expression, NullableMemberChain[]> _negated = new();

    public string? Refuse(Expression node)
    {
        switch (node.NodeType)
        {
            case ExpressionType.OrElse:
                cover(node, ExpressionType.OrElse, ExpressionType.Equal);
                return null;

            case ExpressionType.AndAlso:
                cover(node, ExpressionType.AndAlso, ExpressionType.NotEqual);
                return null;

            case ExpressionType.Not when node is UnaryExpression { Operand: var operand } && node.Type == typeof(bool):
                foreach (var (use, chains) in NegatedUses(operand))
                {
                    _negated.AddOrUpdate(use, chains);
                }

                return null;
        }

        var unsafeChains = node.NodeType == ExpressionType.NotEqual && node is BinaryExpression inequality
            ? ComparedChains(inequality)
            : _negated.TryGetValue(node, out var negated) ? negated : [];

        foreach (var chain in unsafeChains)
        {
            var covered = _covered.TryGetValue(node, out var links) ? links : null;
            var uncovered = chain.NullableLinks().FirstOrDefault(x => covered is null || !covered.Contains(x));

            if (uncovered is not null)
            {
                var name = chain.Path;
                return $"compares '{name}' with <> (or negates a comparison on it), and '{uncovered}' can be null: "
                       + "the database leaves rows where it is null OUT, where the text read as C# would keep them, "
                       + $"so they would be missing without a word. Say what null should do — '... or {name} = null' "
                       + $"keeps those rows, '{name} != null and ...' drops them.";
            }
        }

        return null;
    }

    /// <summary>
    ///     Mark every unsafe use under an <c>or</c> chain that has a <c>X = null</c> disjunct (or an
    ///     <c>and</c> chain with a <c>X != null</c> conjunct) as covered for each nullable link that is a
    ///     prefix of <c>X</c>: on a row where that link is null, the chain has the same value on both readings.
    /// </summary>
    private void cover(Expression node, ExpressionType chainType, ExpressionType nullCheck)
    {
        var operands = new List<Expression>();
        flatten(node, chainType, operands);

        var guards = operands
            .Select(x => NullCheckedChain(x, nullCheck))
            .OfType<NullableMemberChain>()
            .ToList();

        if (guards.Count == 0)
        {
            return;
        }

        foreach (var (use, chain) in UsesUnder(node))
        {
            foreach (var link in chain.NullableLinks())
            {
                if (guards.Any(guard => guard.Covers(chain, link)))
                {
                    _covered.GetOrCreateValue(use).Add(link);
                }
            }
        }
    }

    private static void flatten(Expression node, ExpressionType chainType, List<Expression> operands)
    {
        if (node.NodeType == chainType && node is BinaryExpression binary)
        {
            flatten(binary.Left, chainType, operands);
            flatten(binary.Right, chainType, operands);
        }
        else
        {
            operands.Add(node);
        }
    }

    /// <summary>
    ///     Every unsafe use under <paramref name="root" />: each <c>&lt;&gt;</c>, everything a <c>not</c>
    ///     under it negates, and everything a <c>not</c> ABOVE it negates — which that <c>not</c>, visited
    ///     first, has already recorded.
    /// </summary>
    private IEnumerable<(Expression Use, NullableMemberChain Chain)> UsesUnder(Expression root)
    {
        foreach (var node in Descendants(root))
        {
            if (_negated.TryGetValue(node, out var negatedAbove))
            {
                foreach (var chain in negatedAbove) yield return (node, chain);
            }

            if (node is BinaryExpression { NodeType: ExpressionType.NotEqual } inequality)
            {
                foreach (var chain in ComparedChains(inequality)) yield return (node, chain);
            }
            else if (node is UnaryExpression { NodeType: ExpressionType.Not } not && not.Type == typeof(bool))
            {
                foreach (var (use, chains) in NegatedUses(not.Operand))
                {
                    foreach (var chain in chains) yield return (use, chain);
                }
            }
        }
    }

    /// <summary>A comparison's member chains, unless the other side is null — that is an IS (NOT) NULL.</summary>
    private static NullableMemberChain[] ComparedChains(BinaryExpression comparison)
    {
        if (IsNull(comparison.Left) || IsNull(comparison.Right))
        {
            return [];
        }

        return new[] { NullableMemberChain.From(comparison.Left), NullableMemberChain.From(comparison.Right) }
            .OfType<NullableMemberChain>()
            .ToArray();
    }

    /// <summary>
    ///     Every node under a <c>not</c> whose value SQL can make unknown — a comparison, a string method
    ///     on a member, a nullable flag — with the chains that make it so. Not crossing into a lambda: a
    ///     collection operator is an <c>EXISTS</c>, which is never unknown.
    /// </summary>
    private static List<(Expression Use, NullableMemberChain[] Chains)> NegatedUses(Expression operand)
    {
        var found = new List<(Expression, NullableMemberChain[])>();
        collect(operand);
        return found;

        void collect(Expression? e)
        {
            switch (e)
            {
                case null:
                case LambdaExpression:
                    return;

                case BinaryExpression binary when isComparison(binary.NodeType):
                    if (ComparedChains(binary) is { Length: > 0 } compared) found.Add((binary, compared));
                    return;

                case BinaryExpression binary:
                    collect(binary.Left);
                    collect(binary.Right);
                    return;

                case UnaryExpression unary when unary.NodeType != ExpressionType.Quote:
                    collect(unary.Operand);
                    return;

                case MethodCallExpression call:
                    if (call.Object is not null && call.Object.Type == typeof(string)
                                                && NullableMemberChain.From(call.Object) is { } target)
                    {
                        found.Add((call, [target]));
                    }

                    return;

                case MemberExpression when e.Type == typeof(bool?) && NullableMemberChain.From(e) is { } flag:
                    found.Add((e, [flag]));
                    return;
            }
        }

        static bool isComparison(ExpressionType type) => type is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan
            or ExpressionType.LessThanOrEqual;
    }

    /// <summary>The chain <paramref name="node" /> checks against null with <paramref name="check" />, if it does.</summary>
    private static NullableMemberChain? NullCheckedChain(Expression node, ExpressionType check)
    {
        if (node is not BinaryExpression binary || binary.NodeType != check)
        {
            return null;
        }

        if (IsNull(binary.Right)) return NullableMemberChain.From(binary.Left);
        if (IsNull(binary.Left)) return NullableMemberChain.From(binary.Right);
        return null;
    }

    private static IEnumerable<Expression> Descendants(Expression root)
    {
        var stack = new Stack<Expression>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            foreach (var child in Children(node))
            {
                stack.Push(child);
            }
        }
    }

    private static IEnumerable<Expression> Children(Expression node) => node switch
    {
        BinaryExpression b => [b.Left, b.Right],
        UnaryExpression u => [u.Operand],
        MethodCallExpression m => m.Object is null ? m.Arguments : m.Arguments.Prepend(m.Object),
        LambdaExpression l => [l.Body],
        MemberExpression { Expression: { } owner } => [owner],
        _ => []
    };

    /// <summary>A null constant, through any conversion and any closure Dynamic LINQ wraps a value in.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Reads the captured value out of the closure object Dynamic LINQ itself built for an @n argument; it is never trimmed while the expression that references it is alive.")]
    private static bool IsNull(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression switch
        {
            ConstantExpression constant => constant.Value is null,
            MemberExpression { Expression: ConstantExpression { Value: { } closure }, Member: FieldInfo field }
                => field.GetValue(closure) is null,
            MemberExpression { Expression: ConstantExpression { Value: { } closure }, Member: PropertyInfo property }
                => property.GetValue(closure) is null,
            _ => false
        };
    }
}

/// <summary>
///     A member path off a lambda parameter — <c>ShipTo.City</c> — with which of its links can be null.
/// </summary>
internal sealed class NullableMemberChain
{
    private readonly ParameterExpression _root;
    private readonly MemberInfo[] _members;

    private NullableMemberChain(ParameterExpression root, MemberInfo[] members)
    {
        _root = root;
        _members = members;
        Path = string.Join('.', members.Select(x => x.Name));
    }

    public string Path { get; }

    public static NullableMemberChain? From(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        var members = new List<MemberInfo>();
        while (expression is MemberExpression member)
        {
            members.Add(member.Member);
            expression = member.Expression!;

            if (expression is null)
            {
                return null; // a static member — the allow-list refuses those before this is asked
            }
        }

        if (expression is not ParameterExpression root || members.Count == 0)
        {
            return null;
        }

        members.Reverse();
        return new NullableMemberChain(root, members.ToArray());
    }

    /// <summary>The paths of the chain's links that can be null — <c>ShipTo</c>, then <c>ShipTo.City</c>.</summary>
    public IEnumerable<string> NullableLinks()
    {
        var context = new NullabilityInfoContext();

        for (var i = 0; i < _members.Length; i++)
        {
            if (CanBeNull(_members[i], context))
            {
                yield return string.Join('.', _members.Take(i + 1).Select(x => x.Name));
            }
        }
    }

    /// <summary>
    ///     Whether a <c>X = null</c> / <c>X != null</c> guard on this chain settles the null case of
    ///     <paramref name="link" /> in <paramref name="use" />: the same parameter, and the link is a
    ///     prefix of this chain — a null link makes every longer path null too.
    /// </summary>
    public bool Covers(NullableMemberChain use, string link)
        => ReferenceEquals(_root, use._root)
           && (Path == link || Path.StartsWith(link + ".", StringComparison.Ordinal));

    private static bool CanBeNull(MemberInfo member, NullabilityInfoContext context)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null
        };

        if (type is null)
        {
            return true;
        }

        if (type.IsValueType)
        {
            return Nullable.GetUnderlyingType(type) is not null;
        }

        var state = member switch
        {
            PropertyInfo property => context.Create(property).ReadState,
            FieldInfo field => context.Create(field).ReadState,
            _ => NullabilityState.Unknown
        };

        return state != NullabilityState.NotNull;
    }
}
