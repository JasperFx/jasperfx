using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Dynamic.Core.Exceptions;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace JasperFx.Linq;

/// <summary>
/// Turns predicate and ordering <em>text</em> into a composed <see cref="IQueryable" /> over an element type
/// known only at runtime, so the store's own LINQ provider does the translation (jasperfx#869). Built on
/// <c>System.Linq.Dynamic.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why LINQ and not SQL.</b> A store's provider already resolves member casing, enum storage, soft
/// deletes, tenancy, hierarchies and duplicated-field indexes. Text translated to per-engine SQL by hand
/// would have to re-derive every one of those, and the cheapest mistakes — a member written in the wrong
/// case, an enum compared as a name when it is stored as a number — return zero rows rather than an error.
/// </para>
/// <para>
/// <b>Dialect.</b> C#-flavoured: <c>=</c> or <c>==</c>, <c>and</c> / <c>or</c> / <c>not</c> (lowercase;
/// <c>AND</c> is a syntax error), <c>!=</c> or <c>&lt;&gt;</c>, <c>= null</c> rather than <c>IS NULL</c>,
/// <c>Name.StartsWith("A")</c> rather than <c>LIKE</c>, <c>Status in @0</c> with an array argument,
/// <c>Items.Any(Sku = "X")</c>. Member names are case-insensitive. An enum member compares against its
/// name as a string (<c>Status = "Shipped"</c>). A date literal is read as UTC.
/// </para>
/// <para>
/// <b>Not a sandbox on its own, so it is locked down twice.</b> The parser is configured with no custom
/// types, no <c>new</c>, and no type lookup by name. That still leaves the predefined types reachable
/// (<c>Math</c>, <c>Convert</c>, <c>DateTime.UtcNow</c>, every <c>string</c> instance method), and a LINQ
/// provider evaluates any subtree with no reference to the document <em>in process</em> — so
/// <c>Name = "x".PadLeft(2000000000)</c> would be a memory exhaustion run by the server. Every parsed clause
/// is therefore walked against an allow-list before it is handed to a provider: only a short list of
/// string and collection methods, each of which must touch the document; no static members; no
/// conditionals; no construction beyond dates, times and Guids. Then a size cap on the text, the
/// arguments and the parsed tree. A store adds its own refusals through
/// <see cref="DynamicQueryPolicy.ShapeRules" />.
/// </para>
/// <para>
/// <b>Providers that only implement the generic <c>CreateQuery&lt;T&gt;</c>.</b> Dynamic LINQ's operators
/// finish by calling the non-generic <see cref="IQueryProvider.CreateQuery(Expression)" />, which some
/// providers do not support (Marten's throws). So the text is composed against an empty in-memory stand-in,
/// and the finished expression is re-rooted onto the real source and handed to that source's generic
/// <see cref="IQueryProvider.CreateQuery{TElement}" />. Nothing is executed here; the caller runs the result
/// with its store's own async terminators, and a provider that cannot translate a shape throws then.
/// </para>
/// <para>
/// <b>Native AOT.</b> Not supported, and annotated so: applying text to a runtime type closes generic
/// <c>Queryable</c> operators at runtime, and Dynamic LINQ itself carries no trimming annotations. Callers
/// that can run under AOT should check <c>RuntimeFeature.IsDynamicCodeSupported</c> and answer "not
/// available" — <c>DocumentQueryOptions.ApplyCriteriaTo</c> does exactly that.
/// </para>
/// </remarks>
public static class DynamicQuery
{
    internal const string AotMessage =
        "Applies predicate text to a runtime element type: closes Queryable operators over it with MakeGenericMethod, and System.Linq.Dynamic.Core resolves members reflectively. Not supported under Native AOT or full trimming.";

    private static readonly ParsingConfig _config = new()
    {
        // No custom types, no `new`, no type lookup by simple or quoted name: the text can name the
        // element type's own members and Dynamic LINQ's predefined types, and nothing else.
        CustomTypeProvider = new NoCustomTypes(),
        DisallowNewKeyword = true,
        AllowNewToEvaluateAnyType = false,
        ResolveTypesBySimpleName = false,
        SupportCastingToFullyQualifiedTypeAsString = false,
        LoadAdditionalAssembliesFromCurrentDomainBaseDirectory = false,
        AllowEqualsAndToStringMethodsOnObject = false,
        RestrictOrderByToPropertyOrField = true,

        // The literal traps the #869 spike measured: date text was read in the SERVER's time zone, and
        // number text in its culture. DateTimeIsParsedAsUTC alone does not cover it — a string compared
        // with a DateTimeOffset member, literal or @n argument, goes through a TypeConverter that reads
        // local time — so both date types get a converter that assumes UTC.
        DateTimeIsParsedAsUTC = true,
        NumberParseCulture = CultureInfo.InvariantCulture,
        TypeConverters = new Dictionary<Type, TypeConverter>
        {
            [typeof(DateTimeOffset)] = new UtcDateConverter(typeof(DateTimeOffset)),
            [typeof(DateTime)] = new UtcDateConverter(typeof(DateTime))
        },

        // Member names are matched case-insensitively (the default, restated because it is the point):
        // Marten serializes PascalCase by default and Polecat and Fisher camelCase, and the text is
        // written against the C# type, not the JSON.
        IsCaseSensitive = false
    };

    // Looked up lazily from inside the annotated members: a reflective reference to a
    // [RequiresUnreferencedCode] member from a static initializer is itself an IL2026.
    private static MethodInfo? _applyOfT;
    private static MethodInfo? _validateOfT;

    /// <summary>
    /// Compose <paramref name="query" /> onto <paramref name="source" />. Returns <paramref name="source" />
    /// itself when the query is empty.
    /// </summary>
    /// <exception cref="DynamicQueryException">
    /// The text does not parse, names a member <typeparamref name="T" /> does not have, or is refused by the
    /// allow-list or the policy.
    /// </exception>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IQueryable<T> Apply<T>(IQueryable<T> source, DynamicQueryText query, DynamicQueryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
        {
            return source;
        }

        var standIn = new EnumerableQuery<T>(Array.Empty<T>());
        var composed = compose(standIn, query, policy ?? DynamicQueryPolicy.Default);
        var rerooted = new Reroot(((IQueryable)standIn).Expression, source.Expression).Visit(composed)!;

        return source.Provider.CreateQuery<T>(rerooted);
    }

    /// <summary>
    /// Compose <paramref name="query" /> onto a source whose element type is known only at runtime.
    /// </summary>
    /// <inheritdoc cref="Apply{T}" />
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IQueryable Apply(IQueryable source, DynamicQueryText query, DynamicQueryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _applyOfT ??= typeof(DynamicQuery).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Apply) && m.IsGenericMethodDefinition);

        return (IQueryable)invoke(_applyOfT.MakeGenericMethod(source.ElementType), [source, query, policy])!;
    }

    /// <summary>
    /// Parse and check <paramref name="query" /> against <paramref name="elementType" /> without a source —
    /// to answer "is this text acceptable" before there is anything to run it against.
    /// </summary>
    /// <remarks>
    /// Passing is necessary but not sufficient: a store's provider can still refuse a shape it cannot
    /// translate when the query runs.
    /// </remarks>
    /// <exception cref="DynamicQueryException">As for <see cref="Apply{T}" />.</exception>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static void Validate(Type elementType, DynamicQueryText query, DynamicQueryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(query);
        _validateOfT ??= typeof(DynamicQuery).GetMethod(nameof(validate), BindingFlags.NonPublic | BindingFlags.Static)!;
        invoke(_validateOfT.MakeGenericMethod(elementType), [query, policy]);
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private static void validate<T>(DynamicQueryText query, DynamicQueryPolicy? policy)
    {
        if (!query.IsEmpty)
        {
            compose(new EnumerableQuery<T>(Array.Empty<T>()), query, policy ?? DynamicQueryPolicy.Default);
        }
    }

    private static object? invoke(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    private static Expression compose(IQueryable standIn, DynamicQueryText query, DynamicQueryPolicy policy)
    {
        var arguments = normalizeArguments(query, policy);
        var queryable = standIn;

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var where = query.Where;
            checkLength(DynamicQueryClause.Where, where, policy);

            var before = queryable.Expression;
            queryable = parse(DynamicQueryClause.Where, where, () => queryable.Where(_config, where, arguments));
            checkClause(DynamicQueryClause.Where, where, before, queryable.Expression, policy);
        }

        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            var orderBy = query.OrderBy;
            checkLength(DynamicQueryClause.OrderBy, orderBy, policy);

            var before = queryable.Expression;
            queryable = parse(DynamicQueryClause.OrderBy, orderBy, () => queryable.OrderBy(_config, orderBy));
            checkClause(DynamicQueryClause.OrderBy, orderBy, before, queryable.Expression, policy);
        }

        return queryable.Expression;
    }

    private static IQueryable parse(DynamicQueryClause clause, string text, Func<IQueryable> build)
    {
        try
        {
            return build();
        }
        catch (ParseException e)
        {
            throw new DynamicQueryException(clause, text, e.Message, e.Position, e);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException
                                      or InvalidCastException or OverflowException or NotSupportedException)
        {
            // An argument that will not convert to the member it is compared with surfaces as one of these
            // rather than as a ParseException (a non-Guid string compared to a Guid is a FormatException).
            throw new DynamicQueryException(clause, text, e.Message, null, e);
        }
    }

    private static void checkLength(DynamicQueryClause clause, string text, DynamicQueryPolicy policy)
    {
        if (text.Length > policy.MaxTextLength)
        {
            throw new DynamicQueryException(clause, text[..Math.Min(text.Length, 80)] + "…",
                $"is {text.Length} characters; the limit is {policy.MaxTextLength}.");
        }
    }

    /// <summary>
    /// Every quoted lambda the clause added on top of <paramref name="before" /> — one for a Where, one per
    /// key for an OrderBy / ThenBy chain — walked against the allow-list and the policy's rules.
    /// </summary>
    private static void checkClause(DynamicQueryClause clause, string text, Expression before, Expression after,
        DynamicQueryPolicy policy)
    {
        var node = after;
        while (node is MethodCallExpression call && !ReferenceEquals(node, before))
        {
            foreach (var argument in call.Arguments.Skip(1))
            {
                if (unquote(argument) is LambdaExpression lambda)
                {
                    var refusal = AllowList.Check(lambda.Body, policy);
                    if (refusal is not null)
                    {
                        throw new DynamicQueryException(clause, text, refusal);
                    }
                }
            }

            node = call.Arguments[0];
        }
    }

    private static Expression unquote(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

    // ------------------------------------------------------------------ arguments

    [RequiresDynamicCode(AotMessage)]
    private static object?[] normalizeArguments(DynamicQueryText query, DynamicQueryPolicy policy)
    {
        var arguments = query.Arguments ?? [];
        if (arguments.Count > policy.MaxArguments)
        {
            throw new DynamicQueryException(DynamicQueryClause.Where, query.Where ?? string.Empty,
                $"has {arguments.Count} arguments; the limit is {policy.MaxArguments}.");
        }

        return arguments.Select(NormalizeArgument).ToArray();
    }

    /// <summary>
    /// The CLR value Dynamic LINQ should see for one argument. A <see cref="JsonElement" /> — what an
    /// <c>object</c> becomes after crossing a wire as JSON — is unwrapped to a string, number, boolean,
    /// null or a typed array, so an argument survives serialization without a type tag. A homogeneous
    /// <c>object[]</c> becomes a typed array, so <c>Status in @0</c> compares like with like.
    /// </summary>
    /// <remarks>
    /// Strings are left as strings: Dynamic LINQ converts a string to a <see cref="Guid" />,
    /// <see cref="DateTime" /> or <see cref="DateTimeOffset" /> when it is compared with one, and that
    /// conversion is read as UTC here.
    /// </remarks>
    [RequiresDynamicCode(AotMessage)]
    public static object? NormalizeArgument(object? argument) => argument switch
    {
        JsonElement json => fromJson(json),
        object?[] array => typedArray(array.Select(NormalizeArgument).ToArray()),
        _ => argument
    };

    [RequiresDynamicCode(AotMessage)]
    private static object? fromJson(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => json.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number when json.TryGetInt32(out var i) => i,
        JsonValueKind.Number when json.TryGetInt64(out var l) => l,
        JsonValueKind.Number when json.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => json.GetDouble(),
        JsonValueKind.Array => typedArray(json.EnumerateArray().Select(fromJson).ToArray()),
        _ => json.GetRawText()
    };

    [RequiresDynamicCode(AotMessage)]
    private static object typedArray(object?[] values)
    {
        var types = values.Where(x => x is not null).Select(x => x!.GetType()).Distinct().ToArray();
        if (types.Length != 1)
        {
            return values;
        }

        var elementType = types[0];
        if (values.Any(x => x is null) && elementType.IsValueType)
        {
            elementType = typeof(Nullable<>).MakeGenericType(elementType);
        }

        var typed = Array.CreateInstance(elementType, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            typed.SetValue(values[i], i);
        }

        return typed;
    }

    // ------------------------------------------------------------------ internals

    private sealed class Reroot(Expression from, Expression to) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => ReferenceEquals(node, from) ? to : base.Visit(node);
    }

    /// <summary>Reads date text as UTC unless it carries its own offset.</summary>
    private sealed class UtcDateConverter(Type target) : TypeConverter
    {
        private const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => value is string text
                ? target == typeof(DateTime)
                    ? DateTime.Parse(text, CultureInfo.InvariantCulture, Utc)
                    : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, Utc)
                : base.ConvertFrom(context, culture, value);
    }

    private sealed class NoCustomTypes : IDynamicLinqCustomTypeProvider
    {
        public HashSet<Type> GetCustomTypes() => [];
        public Dictionary<Type, List<MethodInfo>> GetExtensionMethods() => [];
        public Type? ResolveType(string typeName) => null;
        public Type? ResolveTypeBySimpleName(string simpleTypeName) => null;
    }

    /// <summary>
    /// The post-parse allow-list. Returns the first refusal as operator-facing text, or null.
    /// </summary>
    private sealed class AllowList : ExpressionVisitor
    {
        private static readonly HashSet<string> _stringMethods =
        [
            nameof(string.StartsWith), nameof(string.EndsWith), nameof(string.Contains),
            nameof(string.ToLower), nameof(string.ToUpper), nameof(string.ToLowerInvariant),
            nameof(string.ToUpperInvariant), nameof(string.Trim), nameof(string.IsNullOrEmpty),
            nameof(string.IsNullOrWhiteSpace), nameof(string.Equals), nameof(string.Compare),
            nameof(string.CompareOrdinal), nameof(string.CompareTo)
        ];

        private static readonly HashSet<string> _enumerableMethods =
        [
            nameof(Enumerable.Any), nameof(Enumerable.All), nameof(Enumerable.Contains), nameof(Enumerable.Count)
        ];

        private static readonly HashSet<Type> _constructible =
        [
            typeof(DateTime), typeof(DateTimeOffset), typeof(Guid), typeof(TimeSpan), typeof(DateOnly),
            typeof(TimeOnly)
        ];

        private readonly DynamicQueryPolicy _policy;
        private string? _refusal;
        private int _nodes;

        private AllowList(DynamicQueryPolicy policy) => _policy = policy;

        public static string? Check(Expression body, DynamicQueryPolicy policy)
        {
            var visitor = new AllowList(policy);
            visitor.Visit(body);
            return visitor._refusal;
        }

        public override Expression? Visit(Expression? node)
        {
            if (node is null || _refusal is not null)
            {
                return node;
            }

            if (++_nodes > _policy.MaxNodes)
            {
                _refusal = $"is too complex: more than {_policy.MaxNodes} expression nodes.";
                return node;
            }

            _refusal = refuseByNodeType(node);
            if (_refusal is not null)
            {
                return node;
            }

            foreach (var rule in _policy.ShapeRules)
            {
                _refusal = rule.Refuse(node);
                if (_refusal is not null)
                {
                    return node;
                }
            }

            return base.Visit(node);
        }

        private static string? refuseByNodeType(Expression node) => node.NodeType switch
        {
            ExpressionType.Conditional =>
                "uses a conditional (iif, np() or ?:), which no document store translates. Use and / or instead.",
            ExpressionType.Invoke or ExpressionType.Block or ExpressionType.Loop or ExpressionType.Assign
                or ExpressionType.Throw or ExpressionType.Index or ExpressionType.Dynamic or ExpressionType.Extension
                or ExpressionType.MemberInit or ExpressionType.ListInit or ExpressionType.NewArrayBounds =>
                $"uses an expression of kind {node.NodeType}, which a query predicate does not allow.",
            _ => null
        };

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            var allowed = method.DeclaringType == typeof(string)
                ? _stringMethods.Contains(method.Name)
                : method.DeclaringType == typeof(Enumerable)
                    ? _enumerableMethods.Contains(method.Name)
                    : isCollectionContains(method);

            if (!allowed)
            {
                _refusal =
                    $"calls {method.DeclaringType?.Name}.{method.Name}, which a query predicate does not allow. Allowed: string StartsWith / EndsWith / Contains / ToLower / ToUpper / Trim / IsNullOrEmpty / IsNullOrWhiteSpace, and collection Any / All / Contains / Count.";
                return node;
            }

            // A call that does not touch the element is evaluated by the provider IN PROCESS before the
            // query is sent — which is where "x".PadLeft(2000000000) would run.
            if (!ParameterFinder.Touches(node))
            {
                _refusal =
                    $"calls {method.Name} on a constant, which would be evaluated on the server rather than by the query. Pass the computed value as an @n argument instead.";
                return node;
            }

            return base.VisitMethodCall(node);
        }

        private static bool isCollectionContains(MethodInfo method)
            => method.Name == nameof(ICollection<int>.Contains)
               && !method.IsStatic
               && method.GetParameters().Length == 1
               && method.DeclaringType is { } declaring
               && declaring != typeof(string)
               && typeof(System.Collections.IEnumerable).IsAssignableFrom(declaring);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is null)
            {
                _refusal =
                    $"reads the static member {node.Member.DeclaringType?.Name}.{node.Member.Name}. Pass the value as an @n argument instead.";
                return node;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (!_constructible.Contains(node.Type) || node.Arguments.Any(x => x is not ConstantExpression))
            {
                _refusal =
                    $"constructs a {node.Type.Name}, which a query predicate does not allow. Pass the value as an @n argument instead.";
                return node;
            }

            return base.VisitNew(node);
        }
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private bool _found;

        public static bool Touches(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder._found;
        }

        public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found = true;
            return node;
        }
    }
}
