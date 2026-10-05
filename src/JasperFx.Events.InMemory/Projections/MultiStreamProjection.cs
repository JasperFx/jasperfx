using JasperFx.Events.Aggregation;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// A multi-stream projection on the in-memory prototyping store (jasperfx#964): one document per
/// identity the projection groups events by, across any number of streams.
/// </summary>
/// <remarks>
/// Named like Marten's and Fisher's so a projection class moves between stores with a <c>using</c>
/// change. Declare the grouping in the constructor with <c>Identity</c>, <c>Identities</c> or a custom
/// grouper.
/// </remarks>
public abstract class MultiStreamProjection<TDoc, TId>
    : JasperFxMultiStreamProjectionBase<TDoc, TId, IInMemoryDocumentSession, IInMemoryQuerySession>
    where TDoc : notnull
    where TId : notnull;
