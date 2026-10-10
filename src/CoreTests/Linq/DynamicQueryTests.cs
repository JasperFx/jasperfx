using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using JasperFx.Documents;
using JasperFx.Linq;
using Shouldly;

namespace CoreTests.Linq;

public class DynamicQueryTests
{
    public enum OrderStatus
    {
        Open,
        Shipped,
        Cancelled
    }

    public class Address
    {
        public string City { get; set; } = string.Empty;
    }

    public class LineItem
    {
        public string Sku { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public class Order
    {
        public Guid Id { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public OrderStatus Status { get; set; }
        public decimal Total { get; set; }
        public int Quantity { get; set; }
        public bool IsRush { get; set; }
        public DateTimeOffset PlacedAt { get; set; }
        public DateTime ShippedOn { get; set; }
        public string? Notes { get; set; }
        public Address ShipTo { get; set; } = new();
        public List<string> Tags { get; set; } = [];
        public List<LineItem> Items { get; set; } = [];
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Order[] Orders =
    [
        new()
        {
            Id = new Guid("00000001-0000-0000-0000-000000000000"), CustomerName = "Acme", Status = OrderStatus.Open,
            Total = 100.50m, Quantity = 3, PlacedAt = Start, ShipTo = new Address { City = "Austin" },
            Tags = ["vip"], Items = [new LineItem { Sku = "A-1", Quantity = 2 }]
        },
        new()
        {
            Id = new Guid("00000002-0000-0000-0000-000000000000"), CustomerName = "Globex", Status = OrderStatus.Shipped,
            Total = 250m, Quantity = 9, IsRush = true, PlacedAt = Start.AddDays(1),
            ShipTo = new Address { City = "Boston" }, Notes = "fragile"
        },
        new()
        {
            Id = new Guid("00000003-0000-0000-0000-000000000000"), CustomerName = "Initech", Status = OrderStatus.Cancelled,
            Total = 12.25m, Quantity = 1, PlacedAt = Start.AddDays(2), ShipTo = new Address { City = "Austin" },
            Items = [new LineItem { Sku = "B-2", Quantity = 7 }]
        },
        new()
        {
            Id = new Guid("00000004-0000-0000-0000-000000000000"), CustomerName = "Acme", Status = OrderStatus.Shipped,
            Total = 100.50m, Quantity = 6, PlacedAt = Start.AddDays(3), ShipTo = new Address { City = "Austin" },
            Tags = ["vip", "repeat"]
        }
    ];

    private static IQueryable<Order> Source => Orders.AsQueryable();

    private static int[] Ids(IQueryable<Order> queryable) => queryable.AsEnumerable().Select(x => (int)x.Id.ToByteArray()[0]).ToArray();

    private static int[] Apply(string? where, string? orderBy = null, params object?[] args)
        => Ids(DynamicQuery.Apply(Source, new DynamicQueryText(where, orderBy, args)));

    // ---------------------------------------------------------------- the dialect

    [Theory]
    [InlineData("CustomerName = \"Acme\"", new[] { 1, 4 })]
    [InlineData("CustomerName == \"Acme\"", new[] { 1, 4 })]
    [InlineData("customername = \"Acme\"", new[] { 1, 4 })] // members are case-insensitive
    [InlineData("Status = \"Shipped\"", new[] { 2, 4 })] // an enum compares against its name
    [InlineData("Total > 100 and Quantity < 7", new[] { 1, 4 })]
    [InlineData("Total = 100.50", new[] { 1, 4 })]
    [InlineData("IsRush or Quantity = 1", new[] { 2, 3 })]
    [InlineData("not IsRush and Quantity > 2", new[] { 1, 4 })]
    [InlineData("Quantity <> 9 and Quantity != 1", new[] { 1, 4 })]
    [InlineData("ShipTo.City = \"Austin\"", new[] { 1, 3, 4 })]
    [InlineData("Notes = null", new[] { 1, 3, 4 })]
    [InlineData("Notes != null", new[] { 2 })]
    [InlineData("CustomerName.StartsWith(\"Ac\")", new[] { 1, 4 })]
    [InlineData("CustomerName.Contains(\"tech\")", new[] { 3 })]
    [InlineData("Tags.Contains(\"vip\")", new[] { 1, 4 })]
    [InlineData("Items.Any(Quantity > 5)", new[] { 3 })]
    [InlineData("Items.Any(Sku.StartsWith(\"A\"))", new[] { 1 })]
    [InlineData("CustomerName in (\"Globex\", \"Initech\")", new[] { 2, 3 })]
    [InlineData("PlacedAt >= \"2026-09-03\"", new[] { 3, 4 })] // a date literal is read as UTC
    public void the_dialect(string where, int[] expected) => Apply(where).ShouldBe(expected);

    [Fact]
    public void typed_arguments()
    {
        Apply("Total > @0 and CustomerName = @1", null, 100m, "Acme").ShouldBe([1, 4]);
        Apply("PlacedAt >= @0", null, Start.AddDays(2)).ShouldBe([3, 4]);
        Apply("Id = @0", null, Orders[2].Id).ShouldBe([3]);
        Apply("Status in @0", null, (object)new[] { OrderStatus.Open, OrderStatus.Cancelled }).ShouldBe([1, 3]);
    }

    [Fact]
    public void a_string_argument_converts_to_a_guid()
        => Apply("Id = @0", null, Orders[1].Id.ToString().ToUpperInvariant()).ShouldBe([2]);

    [Fact]
    public void arguments_that_crossed_a_wire_as_json_are_unwrapped()
    {
        // What an object?[] looks like after a System.Text.Json round trip: every element a JsonElement.
        var args = JsonSerializer.Deserialize<object?[]>("""[100, "Acme", ["Globex", "Initech"], 100.50, true]""")!;
        args.ShouldAllBe(x => x is JsonElement);

        Apply("Total > @0 and CustomerName = @1", null, args[0], args[1]).ShouldBe([1, 4]);
        Apply("CustomerName in @0", null, args[2]).ShouldBe([2, 3]);
        Apply("Total = @0", null, args[3]).ShouldBe([1, 4]);
        Apply("IsRush = @0", null, args[4]).ShouldBe([2]);
    }

    [Fact]
    public void ordering_with_several_keys()
        => Apply(null, "CustomerName desc, Quantity").ShouldBe([3, 2, 1, 4]);

    [Fact]
    public void predicate_and_ordering_together()
        => Apply("ShipTo.City = \"Austin\"", "Quantity desc").ShouldBe([4, 1, 3]);

    [Fact]
    public void an_empty_query_returns_the_source_itself()
    {
        var source = Source;
        DynamicQuery.Apply(source, new DynamicQueryText(" ", null)).ShouldBeSameAs(source);
    }

    [Fact]
    public void the_non_generic_overload_composes_over_a_runtime_type()
    {
        IQueryable source = Source;
        var result = DynamicQuery.Apply(source, new DynamicQueryText("Quantity > @0", "Quantity desc", [5]));

        result.ElementType.ShouldBe(typeof(Order));
        Ids((IQueryable<Order>)result).ShouldBe([2, 4]);
    }

    // ---------------------------------------------------------------- providers

    [Fact]
    public void works_against_a_provider_that_only_implements_the_generic_create_query()
    {
        // Marten's IQueryProvider.CreateQuery(Expression) throws; Dynamic LINQ's own operators end with it.
        var source = new GenericOnlyQueryable<Order>(Orders);

        var result = DynamicQuery.Apply<Order>(source, new DynamicQueryText("Total > @0", "Quantity desc", [100m]));

        result.ShouldBeOfType<GenericOnlyQueryable<Order>>();
        Ids(result).ShouldBe([2, 4, 1]);
    }

    [Fact]
    public void the_non_generic_overload_also_avoids_the_non_generic_create_query()
    {
        IQueryable source = new GenericOnlyQueryable<Order>(Orders);

        var result = DynamicQuery.Apply(source, new DynamicQueryText("IsRush"));

        result.ShouldBeOfType<GenericOnlyQueryable<Order>>();
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public void a_parse_failure_carries_its_position()
    {
        var ex = Should.Throw<DynamicQueryException>(() => Apply("Quantity > 5 and (Total >"));

        ex.Clause.ShouldBe(DynamicQueryClause.Where);
        ex.Position.ShouldNotBeNull().ShouldBeGreaterThan(15);
        ex.Text.ShouldBe("Quantity > 5 and (Total >");
    }

    [Fact]
    public void an_unknown_member_names_itself_and_where_it_is()
    {
        var ex = Should.Throw<DynamicQueryException>(() => Apply("Quantity > 5 and Weight > 2"));

        ex.Reason.ShouldContain("Weight");
        ex.Position.ShouldBe(17);
    }

    [Fact]
    public void keywords_are_case_insensitive_so_sql_habits_work()
        => Apply("Quantity > 5 AND NOT IsRush OR CustomerName = \"Initech\"").ShouldBe([3, 4]);

    [Fact]
    public void a_date_string_argument_is_read_as_utc_not_server_local_time()
    {
        Apply("PlacedAt >= @0", null, "2026-09-03").ShouldBe([3, 4]);
        Apply("PlacedAt >= @0", null, "2026-09-03T00:00:00+02:00").ShouldBe([3, 4]);
        Apply("PlacedAt >= @0", null, "2026-09-03T00:00:01Z").ShouldBe([4]);
    }

    [Fact]
    public void a_constant_the_parser_evaluates_itself_is_bounded_by_the_text()
    {
        // Dynamic LINQ folds Uri("...") into a constant at parse time; reading a member of a constant is
        // cheap, and the allocation is bounded by the text length cap. A constructor it does NOT fold —
        // String('x', n) — is a NewExpression, and the allow-list refuses it (see the hostile cases).
        Apply("Uri(\"http://x\").Host = \"x\"").Length.ShouldBe(4);
    }

    [Fact]
    public void an_ordering_failure_is_attributed_to_the_ordering()
        => Should.Throw<DynamicQueryException>(() => Apply("IsRush", "Weight desc")).Clause.ShouldBe(DynamicQueryClause.OrderBy);

    [Fact]
    public void an_argument_that_will_not_convert_is_a_refusal_not_a_crash()
        => Should.Throw<DynamicQueryException>(() => Apply("Id = @0", null, "not-a-guid")).Clause.ShouldBe(DynamicQueryClause.Where);

    [Fact]
    public void a_non_boolean_predicate_is_refused()
        => Should.Throw<DynamicQueryException>(() => Apply("Quantity + 1"));

    // ---------------------------------------------------------------- lock-down

    [Theory]
    [InlineData("System.IO.File.Exists(\"/etc/passwd\")")]
    [InlineData("File.Exists(\"/etc/passwd\")")]
    [InlineData("System.Diagnostics.Process.Start(\"/usr/bin/true\") != null")]
    [InlineData("Environment.MachineName != null")]
    [InlineData("System.Environment.GetEnvironmentVariable(\"HOME\") != null")]
    [InlineData("CustomerName.GetType().Assembly.FullName != null")]
    [InlineData("it.GetType().Name = \"Order\"")]
    [InlineData("\"\".GetType().Assembly.GetType(\"System.IO.File\") != null")]
    [InlineData("typeof(System.IO.File) != null")]
    [InlineData("new System.Net.WebClient().DownloadString(\"http://127.0.0.1:1/\") != null")]
    [InlineData("System.Activator.CreateInstance(\"System.Diagnostics.Process\") != null")]
    [InlineData("AppDomain.CurrentDomain.FriendlyName != null")]
    [InlineData("System.Reflection.Assembly.GetExecutingAssembly() != null")]
    [InlineData("CustomerName.ToString() = \"Acme\"")]
    [InlineData("it.ToString() != null")]
    [InlineData("it.Equals(it)")]
    [InlineData("Object.ReferenceEquals(it, it)")]
    [InlineData("Math.Abs(Quantity) > 5")]
    [InlineData("Convert.ToInt32(Total) > 5")]
    [InlineData("DateTime.UtcNow > PlacedAt")]
    [InlineData("Guid.NewGuid() != Id")]
    [InlineData("CustomerName = \"x\".PadLeft(50)")]
    [InlineData("CustomerName = \"x\".PadLeft(2000000000)")]
    [InlineData("CustomerName.PadLeft(50).Length > 1")]
    [InlineData("new(CustomerName as X).X = \"Acme\"")]
    [InlineData("CustomerName = String('x', 2000000000)")]
    [InlineData("CustomerName = \"x\" + \"y\"")]
    [InlineData("np(Notes) = null")]
    [InlineData("iif(IsRush, 1, 2) = 1")]
    [InlineData("\"abc\".ToUpper() = CustomerName")]
    [InlineData("CustomerName = String.Concat(\"A\", \"cme\")")]
    [InlineData("String.Join(\",\", Tags) = \"vip\"")]
    public void hostile_predicates_are_refused(string where)
    {
        var ex = Should.Throw<DynamicQueryException>(() => Apply(where));
        ex.Clause.ShouldBe(DynamicQueryClause.Where);
    }

    [Fact]
    public void a_hostile_ordering_is_refused_too()
        => Should.Throw<DynamicQueryException>(() => Apply(null, "CustomerName.PadLeft(2000000000)"));

    [Fact]
    public void the_text_length_is_capped()
    {
        var policy = DynamicQueryPolicy.Default with { MaxTextLength = 20 };
        var ex = Should.Throw<DynamicQueryException>(() =>
            DynamicQuery.Apply(Source, new DynamicQueryText("CustomerName = \"Acme\" or IsRush"), policy));

        ex.Reason.ShouldContain("limit is 20");
    }

    [Fact]
    public void the_default_length_cap_holds()
    {
        var where = string.Join(" or ", Enumerable.Repeat("Quantity = 1", 200));
        Should.Throw<DynamicQueryException>(() => Apply(where)).Reason.ShouldContain("2000");
    }

    [Fact]
    public void the_tree_size_is_capped()
    {
        var policy = DynamicQueryPolicy.Default with { MaxNodes = 10 };
        var where = string.Join(" or ", Enumerable.Repeat("Quantity = 1", 10));

        Should.Throw<DynamicQueryException>(() => DynamicQuery.Apply(Source, new DynamicQueryText(where), policy))
            .Reason.ShouldContain("too complex");
    }

    [Fact]
    public void the_argument_count_is_capped()
    {
        var policy = DynamicQueryPolicy.Default with { MaxArguments = 2 };

        Should.Throw<DynamicQueryException>(() =>
                DynamicQuery.Apply(Source, new DynamicQueryText("Quantity = @0", null, [1, 2, 3]), policy))
            .Reason.ShouldContain("limit is 2");
    }

    // ---------------------------------------------------------------- store shape rules

    [Fact]
    public void a_store_can_refuse_member_access_on_a_date()
    {
        var policy = DynamicQueryPolicy.Default.WithRules(
            DynamicQueryShapeRules.NoMemberAccessOn<DateTimeOffset>("this store compares dates whole."));

        Should.Throw<DynamicQueryException>(() =>
                DynamicQuery.Apply(Source, new DynamicQueryText("PlacedAt.Year = 2026"), policy))
            .Reason.ShouldBe("this store compares dates whole.");

        // The date itself is still fine.
        DynamicQuery.Apply(Source, new DynamicQueryText("PlacedAt > @0", null, [Start]), policy).Count().ShouldBe(3);
    }

    [Fact]
    public void a_store_can_refuse_a_collection_size_property()
    {
        var policy = DynamicQueryPolicy.Default.WithRules(DynamicQueryShapeRules.NoCollectionSizeProperty("use Any()."));

        Should.Throw<DynamicQueryException>(() =>
            DynamicQuery.Apply(Source, new DynamicQueryText("Items.Count > 0"), policy));

        // A string's Length is not a collection size.
        DynamicQuery.Apply(Source, new DynamicQueryText("CustomerName.Length = 4"), policy).Count().ShouldBe(2);
    }

    [Fact]
    public void a_store_can_refuse_a_method()
    {
        var policy = DynamicQueryPolicy.Default.WithRules(
            DynamicQueryShapeRules.NoMethod(typeof(Enumerable), nameof(Enumerable.Any), "no Any here."));

        Should.Throw<DynamicQueryException>(() =>
            DynamicQuery.Apply(Source, new DynamicQueryText("Items.Any(Quantity > 1)"), policy)).Reason.ShouldBe("no Any here.");
    }

    // ---------------------------------------------------------------- validation without a source

    [Fact]
    public void validate_accepts_good_text_and_refuses_bad()
    {
        DynamicQuery.Validate(typeof(Order), new DynamicQueryText("Total > @0", "PlacedAt desc", [1m]));

        Should.Throw<DynamicQueryException>(() => DynamicQuery.Validate(typeof(Order), new DynamicQueryText("Weight > 1")))
            .Position.ShouldBe(0);
    }

    // ---------------------------------------------------------------- DocumentQueryOptions

    [Fact]
    public void document_options_without_criteria_leave_the_source_alone()
    {
        var source = Source;
        new DocumentQueryOptions(1, 10).HasCriteria().ShouldBeFalse();
        new DocumentQueryOptions(1, 10).ApplyCriteriaTo(source).ShouldBeSameAs(source);
    }

    [Fact]
    public void document_options_apply_where_order_and_tie_breaker()
    {
        var options = new DocumentQueryOptions(1, 10)
        {
            Where = "CustomerName = @0 or Total < @1", Arguments = ["Acme", 50m], OrderBy = "Total desc"
        };

        options.HasCriteria().ShouldBeTrue();
        // 1 and 4 tie on Total; the tie-breaker orders them by Id.
        Ids(options.ApplyCriteriaTo(Source, tieBreaker: "Id")).ShouldBe([1, 4, 3]);
    }

    [Fact]
    public void document_options_turn_a_parse_failure_into_a_positioned_refusal()
    {
        var options = new DocumentQueryOptions(1, 10) { Where = "Quantity > 5 and Weight > 2" };

        var ex = Should.Throw<DocumentCriteriaNotSupportedException>(() => options.ApplyCriteriaTo(Source));

        ex.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
        ex.Position.ShouldBe(17);
        ex.InnerException.ShouldBeOfType<DynamicQueryException>();
    }

    [Fact]
    public void document_options_attribute_an_ordering_failure_to_order_by()
    {
        var options = new DocumentQueryOptions(1, 10) { Where = "IsRush", OrderBy = "Weight" };

        Should.Throw<DocumentCriteriaNotSupportedException>(() => options.ApplyCriteriaTo(Source))
            .Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderBy));
    }

    [Fact]
    public void untranslatable_names_the_first_criterion_and_keeps_the_provider_message()
    {
        var failure = new NotSupportedException("no Year here");

        var where = new DocumentQueryOptions(1, 10) { Where = "IsRush", OrderBy = "Total" }.Untranslatable(failure);
        where.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
        where.Message.ShouldContain("no Year here");
        where.InnerException.ShouldBeSameAs(failure);

        new DocumentQueryOptions(1, 10) { OrderBy = "Total" }.Untranslatable(failure)
            .Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderBy));
    }

    [Fact]
    public void translation_failures_are_told_apart_from_infrastructure_failures()
    {
        DocumentQueryCriteria.IsTranslationFailure(new NotSupportedException()).ShouldBeTrue();
        DocumentQueryCriteria.IsTranslationFailure(new JasperFx.BadLinqExpressionException("x")).ShouldBeTrue();
        DocumentQueryCriteria.IsTranslationFailure(new InvalidOperationException()).ShouldBeTrue();

        DocumentQueryCriteria.IsTranslationFailure(new OperationCanceledException()).ShouldBeFalse();
        DocumentQueryCriteria.IsTranslationFailure(new TimeoutException()).ShouldBeFalse();
        DocumentQueryCriteria.IsTranslationFailure(new IOException()).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- a Marten-shaped provider

    /// <summary>
    /// A queryable whose provider throws from the non-generic <see cref="IQueryProvider.CreateQuery(Expression)" />,
    /// as Marten's does, and otherwise delegates to LINQ to objects.
    /// </summary>
    private sealed class GenericOnlyQueryable<T> : IOrderedQueryable<T>, IQueryProvider
    {
        private readonly IQueryable<T> _inner;

        public GenericOnlyQueryable(IEnumerable<T> items) : this(items.AsQueryable().Expression) { }

        private GenericOnlyQueryable(Expression expression)
        {
            Expression = expression;
            _inner = new EnumerableQuery<T>(expression);
        }

        public Type ElementType => typeof(T);
        public Expression Expression { get; }
        public IQueryProvider Provider => this;

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            => (IQueryable<TElement>)(object)new GenericOnlyQueryable<T>(expression);

        public object? Execute(Expression expression) => throw new NotSupportedException();
        public TResult Execute<TResult>(Expression expression) => _inner.Provider.Execute<TResult>(expression);
    }
}
