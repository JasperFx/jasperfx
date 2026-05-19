namespace JasperFx.Events.Aggregation;

/// <summary>
/// Marks an identity-less "boundary" aggregate — one with conventional
/// <c>Apply</c> / <c>Create</c> methods but no single-stream identity (no <c>Id</c>
/// property and no <see cref="AggregateIdentityAttribute"/>) — so the source
/// generator emits an evolver for it.
/// </summary>
/// <remarks>
/// Boundary aggregates span multiple streams by tag rather than being keyed to a
/// single stream id. They are fetched via the Dynamic Consistency Boundary (DCB)
/// surface — e.g. Marten's <c>FetchForWritingByTags&lt;T&gt;</c> /
/// <c>AggregateByTagsAsync&lt;T&gt;</c>, registered through
/// <c>RegisterTagType&lt;...&gt;().ForAggregate&lt;T&gt;()</c>.
///
/// Without a single-stream identity the generator cannot infer a <c>TId</c>, so by
/// default it emits nothing and the runtime throws
/// <c>InvalidProjectionException: No source-generated dispatcher found</c> when the
/// aggregate is fetched. Applying this attribute to the (partial) aggregate type
/// opts it into evolver generation: the generator emits an
/// <c>IGeneratedSyncEvolver&lt;TDoc, string&gt;</c> built from the type's
/// <c>Apply</c> / <c>Create</c> methods (the <c>string</c> TId is vestigial — it
/// matches the <c>SingleStreamProjection&lt;T, string&gt;</c> the DCB aggregator
/// builds and is never used by boundary-aggregate dispatch).
///
/// The attribute must sit on the aggregate type in <b>its own defining assembly</b>:
/// that is the compilation the generator emits the
/// <see cref="GeneratedEvolverAttribute"/> into, and it is the assembly the runtime
/// scans (<c>typeof(TDoc).Assembly</c>) when resolving the evolver. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/324">#324</see>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public class BoundaryAggregateAttribute : Attribute
{
}
