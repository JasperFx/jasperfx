# Similarity Search

`JasperFx.Events.Vectors` holds the parts of vector and hybrid search that every Critter Stack
document store needs to agree on: the contracts a store implements, the options a caller passes, the
fusion arithmetic, and the store-independent body of a vector projection.

It contains no SQL and talks to no database. Marten (through `Marten.PgVector`), Polecat and Fisher
each implement it over their own engine.

## Why these types are shared

Each store shipped its own `HybridSearchOptions`, its own fusion, and its own vector projection map.
They disagreed, and the disagreements were invisible:

- **The distance default.** Marten's options defaulted to `Cosine`, so an index declared for L2 was
  searched by cosine while Polecat and Fisher searched by L2. Same code, three stores, three answers,
  no error anywhere. `HybridSearchOptions.Distance` now defaults to `null`, meaning *the metric the
  index declared* — see [the default that matters](#the-default-that-matters).
- **The text style names.** `Plain`/`Phrase` in one store, `PlainText`/`WebStyle` in the others.
- **The projection map.** One store's content selector saw the event body, another's saw the
  `IEvent<T>` wrapper; one was Guid-only; one deleted by stream id regardless of the configured id
  selector, so a projection keyed on a payload member deleted nothing.

## The search contract

`IDocumentSearchOperations` is what a store implements, reached from a store-agnostic session
through `IDocumentReadOperations.Search`:

```csharp
var nearest = await session.Search.VectorSearchWithScoresAsync<Memory>(
    x => x.Embedding,
    queryVector,
    limit: 10,
    filter: x => x.Scope == "project-a");

foreach (var match in nearest)
{
    // Distance, never similarity: smaller is closer, on every store and every metric.
    Console.WriteLine($"{match.Document.Title} at {match.Distance:F3}");
}
```

The document-only forms — `VectorSearchAsync` and `HybridSearchAsync` — are extension methods over
the scored ones, so an implementing store writes exactly two methods.

::: tip Why `Search` is a property rather than members on the session
Every store already ships extension methods named `VectorSearchWithScoresAsync` and
`HybridSearchWithScoresAsync` on its own `IQuerySession`. Members of those names on an interface the
session implements would win overload resolution over those extensions at every existing call
site — silently, and with different behavior, because it is the store's own extension that knows its
tenancy and soft-delete predicates. An accessor cannot collide.
:::

A store that has not implemented search yet leaves the throwing default in place, so it picks up a
new JasperFx without a compile break. What holds a store to the real behavior is the shared
compliance suite, not the compiler.

### Filters run in the database

The `filter` predicate is applied **before** `limit`, and before each hybrid leg's candidate depth,
so the result is the top-k of the filtered set rather than the filtered remains of the top-k.

That distinction is not cosmetic. Filtering afterwards can return *nothing at all*:

```csharp
// The old shape. If the 100 rows nearest overall all belong to other scopes, this is empty —
// even though thousands of matching rows exist.
var hits = await session.Search.VectorSearchWithScoresAsync<Memory>(x => x.Embedding, q, limit: 100);
var top = hits.Where(h => h.Document.Scope == "project-a").Take(10);
```

The store's own implicit predicates — conjoined tenancy, soft deletes, a document hierarchy — apply
as they would to `Query<T>()`. The filter is in addition to those, not instead of them.

::: warning Approximate indexes and recall
An approximate index bounds how many candidates one index scan considers, and a filter is applied to
what that scan produced, so a selective filter can thin the result below `limit`. Marten sizes
pgvector's `hnsw.ef_search` to each search (never below pgvector's default of 40, never above its
ceiling of 1000) and, on pgvector 0.8 and later, turns on iterative scan so a filtered search still
returns its limit. An indexed search asking for more than 1000 rows, or a selective filter on an older
pgvector, can still come back short. Stores doing an exact scan, Polecat and Fisher today, have no
such bound. Each store documents its own recall limits.
:::

## Hybrid search

A full-text ranking and a vector ranking of the same documents, fused by reciprocal rank fusion:

```csharp
var results = await session.Search.HybridSearchWithScoresAsync<Memory>(
    x => x.Embedding,
    text: "connection pool timeouts",
    query: queryVector,
    limit: 10,
    options: new HybridSearchOptions(CandidateDepth: 200));
```

`HybridMatch<T>.Score` is **larger is better**, the opposite of `VectorMatch<T>.Distance`. Its
absolute value means little on its own; what it supports is a floor, or a comparison between results
of the same query.

### The default that matters

```csharp
public sealed record HybridSearchOptions(
    int K = 60,
    int? CandidateDepth = null,
    DistanceFunction? Distance = null,
    HybridTextStyle TextStyle = HybridTextStyle.PlainText,
    string? RegConfig = null,
    IReadOnlyList<double>? ColumnWeights = null);
```

- **`Distance = null` means "the metric the index declared"** and should almost always be left alone.
  An index built for one metric does not answer another one well.
- **`CandidateDepth = null` means `max(limit × 4, 50)`**, and it must be at least `limit`. Reading
  only `limit` from each leg defeats hybrid search: a document ranked 40th by one leg and first by the
  other is exactly what it exists to surface. A depth below `limit` is refused.
- **`TextStyle` has two members, and the shortness is deliberate.** Both are safe to hand a search
  box's raw contents. A store's raw query syntax is absent because it can be malformed, and a
  malformed query in one leg of a fused search fails the *whole* call.
- **`RegConfig`** is meaningful only on a Postgres-backed store; others ignore it.
- **`ColumnWeights`** weighs the text leg's indexed columns against each other, one weight per column
  in the order the full-text index declared them. Reciprocal rank fusion reads the text leg's *order*,
  so weighting a title above a body changes which documents make the candidate depth and how they
  fuse. It is honoured only by a store that ranks per column at query time, which today is **Fisher**,
  whose FTS5 `bm25()` takes one weight per column. **Marten** weights at index time through
  `WeightedFullTextIndex` and **Polecat** ranks a single member, so both refuse a non-null value by
  name rather than ignore it. A weight count that doesn't match the index, an empty list, and a
  non-finite weight are all refused, because a ranking that is quietly not the one you asked for is
  the defect this option exists to remove.

```csharp
// Fisher: a hit in the first indexed column counts three times a hit in the second
var results = await session.Search.HybridSearchWithScoresAsync<Memory>(
    x => x.Embedding,
    text: "connection pool timeouts",
    query: queryVector,
    limit: 10,
    options: new HybridSearchOptions(ColumnWeights: [3.0, 1.0]));
```

## Reciprocal rank fusion

`ReciprocalRankFusion.Fuse` is a pure function over ranked lists — no database, no store:

```csharp
var fused = ReciprocalRankFusion.Fuse(textLeg, vectorLeg, doc => doc.Id, limit: 10);
```

`score(d) = Σ 1 / (k + rank(d))` over the legs that found it, ranks 1-based.

**It reads ordinal position only, never the legs' own scores.** A bm25 or `ts_rank` relevance and a
cosine distance are not on a comparable scale and do not run in the same direction, so normalising
them into one number means picking constants that are wrong for somebody's corpus. Because RRF needs
only each leg's ordering, the two need no calibration and the behavior does not move when the
embedding model or the tokenizer changes.

**Legs need not be the same type**, because fusion is by key. That is the case a store's own hybrid
search cannot serve: a snapshot document carrying the full-text index fused with a separate embedding
document carrying the vector — exactly the shape a vector projection writes.

Ties break deterministically (score, then best rank, then identity), which is not tidiness: two
documents found at the same rank by one leg and by neither in the other have identical scores, and
without a total order the page a caller gets differs between runs.

::: tip k is flatter than it looks
At the default `k = 60` the curve is nearly flat, so `1/61 + 1/63` (first in one leg, last in the
other) slightly beats `1/62 + 1/62` (second in both). RRF rewards agreement, but not enough to
overtake a document that topped a leg. Lower `k` to sharpen that.
:::

## Vector projections

A vector projection keeps an embedding in sync with a stream. `VectorProjectionMap<TId>` declares
where the text comes from, and `VectorEmbeddingPlan<TId>` does everything that is identical across
stores: fold a page, skip unchanged documents, and make one batched model call.

```csharp
var map = new VectorProjectionMap<Guid>()
    .Map<MemoryRecorded>(e => $"{e.Data.Title}\n{e.Data.Body}", e => e.Data.Id)
    .Delete<MemoryForgotten>(e => e.Data.Id);
```

The content selector receives the `IEvent<T>` wrapper rather than the bare body, so metadata can
contribute to the embedded text or to the identity. `Delete` has no overload without an id selector:
making the write and the delete structurally incapable of disagreeing beats checking that they agree,
and the common case costs `e => e.StreamId`.

A selector that throws is **not** swallowed. Catching it and returning `null` reads to the caller as
"no content for this event", so a buggy selector would drop the document out of the index with
nothing reported anywhere. A throw faults the shard, which is what the daemon's error handling is for.

### Content from aggregate state

A selector that sees one event cannot keep an embedding correct across partial updates. Given
`MemoryRevised { Title = null, Body = "new body", Tags = null }` where `null` means "unchanged",
returning the new body re-embeds the document without its title and tags, and returning `null` leaves
the embedding stale. Both answers are wrong, and there used to be no third one:

```csharp
var map = new VectorProjectionMap<Guid>()
    .MapFromAggregate<MemorySnapshot>(
        snapshot => $"{snapshot.Title}\n{snapshot.Body}\n{string.Join(' ', snapshot.Tags)}",
        (typeof(MemoryRecorded), e => e.StreamId),
        (typeof(MemoryRevised), e => e.StreamId),
        (typeof(MemoryTagged), e => e.StreamId));
```

The triggering events say *when* to rebuild; the aggregate says *what* the text is. One load per
affected stream per page, not per event.

::: warning Where the aggregate comes from is an ordering decision
The two sound sources are the **inline snapshot** — which a store must refuse to register unless
`TAggregate` really is projected inline — and **live aggregation** up to the page's last event, which
costs one read per affected stream per page.

Reading an **async** snapshot is the wrong one. The daemon does not order shards against each other,
so a vector projection on one shard can see a snapshot another shard has not caught up to, and the
embedding is then silently built from stale state.
:::

### The plan

```csharp
var plan = VectorEmbeddingPlan<Guid>.Build(map, page.Events);

foreach (var id in plan.AggregateIds)
{
    plan.ApplyAggregate(id, map, await LoadSnapshotAsync(id, token));
}

var writes = await plan.ResolveAsync(
    embeddingProvider,
    (ids, ct) => ReadStoredHashesAsync(ids, ct),
    token);

foreach (var write in writes)
{
    await UpsertAsync(write.Id, write.Content, write.ContentHash, write.Embedding, token);
}

foreach (var id in plan.Deletions)
{
    await DeleteAsync(id, token);
}
```

Only the two storage calls are the store's. Everything else — last-content-per-id within a page,
SHA-256 content hashing, dropping unchanged documents, and the single batched
`IEmbeddingProvider.GenerateEmbeddingsAsync` call — is shared.

**Unchanged content costs no model call at all.** That is the whole economics of a vector projection
over partial-update events, which usually change nothing the embedded text mentions. It is also why
the hash is *persisted* beside the vector rather than recomputed: `VectorEmbeddingPlan<TId>.HashOf`
is lowercase hex SHA-256 of the UTF-8 text, spelled out in one place because two stores hashing
differently is invisible until a corpus moves between them, and a store that changed its spelling
would silently re-embed everything it holds.

## Holding a store to it

`JasperFx.Events.ComplianceTests` ships `DocumentSearchCompliance`, the shared suite every store
enrolls. It is **not** a relevance suite — how good a ranking is depends on the tokenizer, the index
parameters and the embedding model, none of which are shared. It pins the things a store-agnostic
caller cannot discover for itself:

- nearest comes first, and `Distance` is a **distance** on every metric — smaller is closer
- a hybrid `Score` runs the other way, larger is better
- the store's own implicit predicates — tenancy, soft deletes, a document hierarchy — apply as they
  do to `Query<T>()`
- a `filter` narrows **before** the limit, so a predicate excluding every globally-nearest row still
  returns the full limit
- a query vector of the wrong length is refused rather than answered

Each of those was divergent in at least one store when the suite was written, which is why it exists.
Enroll it with an empty subclass, flip `SupportsVectorSearch` / `SupportsHybridSearch` on the
fixture, and replay `DocumentComplianceConfig.VectorIndexes` and `FullTextIndexes` — a vector search
reads a *declared* index, so a fixture that skips the declaration fails every fact rather than
skipping them.

## Embedding providers

`IEmbeddingProvider` is the one thing you write. It takes an array because embedding models charge
per call, not per text:

```csharp
public class MyProvider : IEmbeddingProvider
{
    public int Dimensions => 768;

    public async Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(
        string[] texts, CancellationToken ct = default)
    {
        if (texts.Length == 0) return [];
        // one model call for the whole batch
    }
}
```

Return one vector per input, **in input order** — stores and `VectorEmbeddingPlan<TId>` pair them by
position and have no other way to match a vector to its text. An empty input must return empty
without calling the model; a batch in which every text was skipped by hash comparison then costs
nothing.

A model call is a network round trip, so where it is called from matters more than what it returns.
Run from a session's pre-commit hook it holds the store's write transaction open for that round trip,
which on a single-writer store blocks every other writer for the duration. That is why **Polecat and
Fisher refuse to register a vector projection as anything but async**. Marten allows inline, where the
writes still commit with the caller's events, but async is the one to prefer in production. On every
store, a map built with `MapFromAggregate` needs async, because it aggregates committed events and an
inline pass runs before the triggering event has committed.

## Store documentation

Each store documents its own engine, recall limits, and refusals:

- **Marten** (PostgreSQL, through `Marten.PgVector`):
  [vector, hybrid search and vector projections](https://martendb.io/documents/pgvector),
  [full-text search](https://martendb.io/documents/full-text)
- **Polecat** (SQL Server 2025):
  [full-text search](https://polecat.jasperfx.net/documents/querying/full-text-search),
  [vector search](https://polecat.jasperfx.net/documents/querying/vector-search),
  [hybrid search](https://polecat.jasperfx.net/documents/querying/hybrid-search),
  [store-neutral search](https://polecat.jasperfx.net/documents/querying/store-neutral-search),
  [vector projections](https://polecat.jasperfx.net/events/projections/vector-projections)
- **Fisher** (SQLite):
  [full-text search](https://fisher.jasperfx.net/documents/querying/linq/full-text),
  [vector search](https://fisher.jasperfx.net/documents/querying/vector-search),
  [hybrid search](https://fisher.jasperfx.net/documents/querying/hybrid-search),
  [store-agnostic search](https://fisher.jasperfx.net/documents/querying/store-agnostic-search),
  [vector projections](https://fisher.jasperfx.net/events/projections/vector)
