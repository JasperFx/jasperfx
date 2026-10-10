# Dynamic LINQ Queries

`JasperFx.Linq.DynamicQuery` turns a predicate and an ordering written as **text** into a composed
`IQueryable` over an element type known only at runtime. The store's own LINQ provider then does the
translation, so member casing, enum storage, soft deletes, tenancy, hierarchies and duplicated-field
indexes stay the store's job. It is how the document diagnostics surface (`DocumentQueryOptions.Where` /
`OrderBy`) and the `stream-query` command's `--where` / `--order-by` flags work. It is built on
[System.Linq.Dynamic.Core](https://dynamic-linq.net/).

```cs
IQueryable<Order> orders = session.Query<Order>();

var filtered = DynamicQuery.Apply(orders,
    new DynamicQueryText("Status = \"Open\" and Total > @0", "PlacedAt desc", [100m]));

// Still unexecuted — run it with the store's own async terminators.
var page = await filtered.Take(25).ToListAsync();
```

The non-generic overload takes an `IQueryable` whose element type is known only as a `System.Type`, and
`DynamicQuery.Validate(Type, DynamicQueryText)` checks text against a type without a source.

## The dialect

C#-flavoured, not SQL:

| Want | Write |
|---|---|
| equality | `Status = "Open"` or `Status == "Open"` |
| inequality | `Quantity != 5` or `Quantity <> 5` |
| logic | `and` / `or` / `not` (case-insensitive: `AND` works too) |
| null | `Notes = null`, `Notes != null` |
| text | `Name.StartsWith("Ac")`, `Name.Contains("tech")`, `Name.EndsWith("x")` |
| a set | `Status in @0` with an array argument, or `Name in ("a", "b")` |
| nested | `ShipTo.City = "Austin"` |
| collections | `Tags.Contains("vip")`, `Items.Any(Quantity > 5)` |
| ordering | `PlacedAt desc, Id` |

Member names are matched **case-insensitively** against the C# type, never against the JSON. An enum
member compares against its name as a string (`Status = "Shipped"`).

### Prefer typed `@n` arguments to literals

`Total > @0` with a `decimal` argument has no number-format or time-zone question to answer. When text
must stand in for a value, date text is read as **UTC** unless it carries its own offset, and numbers are
read with the invariant culture. Arguments that crossed a wire as JSON (`JsonElement`) are unwrapped to
strings, numbers, booleans and typed arrays, and a string is converted to a `Guid`, `DateTime` or
`DateTimeOffset` when it is compared with one.

## Locked down twice

The parser runs with no custom types, no `new`, and no type lookup by name, so `System.IO.File`,
`Process`, `Activator`, `typeof` and `GetType()` are unreachable. That is not enough on its own: Dynamic
LINQ's predefined types (`Math`, `Convert`, `DateTime.UtcNow`, every `string` instance method) stay
reachable, and a LINQ provider evaluates any subtree that does not touch the document **in process**, so
`Name = "x".PadLeft(2000000000)` would run on the server.

Every parsed clause is therefore checked against an allow-list before a provider sees it:

- only the string methods `StartsWith`, `EndsWith`, `Contains`, `ToLower`, `ToUpper`, `Trim`,
  `IsNullOrEmpty`, `IsNullOrWhiteSpace` and comparisons, plus collection `Any`, `All`, `Contains` and
  `Count` — and each call must touch the document;
- no static members, no conditionals (`iif`, `np()`), no construction beyond dates, times and Guids;
- caps on the text length (2,000), the number of arguments (64) and the parsed tree (500 nodes).

A refusal or a parse failure throws `DynamicQueryException`, which carries the `Clause`, the `Reason`
written for the person who typed the text, and — for a parse failure — the `Position` it stopped at.

## Store shape rules

Most shapes a provider cannot translate fail honestly when the query runs. A few translate into SQL that
runs and returns the **wrong rows**. A store refuses those up front with a `DynamicQueryPolicy`:

```cs
var policy = DynamicQueryPolicy.Default.WithRules(
    DynamicQueryShapeRules.NoMemberAccessOn<DateTimeOffset>("compare the whole date with an @n argument."),
    DynamicQueryShapeRules.NoCollectionSizeProperty("use Any() instead of Count."));
```

## Document diagnostics

Stores apply `DocumentQueryOptions.Where` / `OrderBy` with `ApplyCriteriaTo`, which maps every failure
onto the contract's `DocumentCriteriaNotSupportedException` (with `Position` for a parse failure):

```cs
var queryable = options.ApplyCriteriaTo(session.Query<T>(), tieBreaker: "Id", policy: storePolicy);
try
{
    total = await queryable.CountAsync(token);
}
catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
{
    throw options.Untranslatable(e);
}
```

A store that cannot apply criteria at all still throws the refusal — never the unfiltered page.

## Native AOT

Not supported, and annotated `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]`: applying text to a
runtime type closes generic `Queryable` operators at runtime. `DocumentQueryCriteria.IsAvailable` is false
under AOT, and `ApplyCriteriaTo` refuses rather than tries.
