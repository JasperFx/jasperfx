using JasperFx.Events.Aggregation;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// A single-stream projection on the in-memory prototyping store (jasperfx#964): one document per
/// stream, folded from that stream's events.
/// </summary>
/// <remarks>
/// Named like Marten's and Fisher's so a projection class moves between stores with a <c>using</c>
/// change. The base is store-specific by design (decided on jasperfx#962): a projection class is closed
/// over its store's session types.
/// </remarks>
public class SingleStreamProjection<TDoc, TId>
    : JasperFxSingleStreamProjectionBase<TDoc, TId, IInMemoryDocumentSession, IInMemoryQuerySession>
    where TDoc : notnull
    where TId : notnull;
