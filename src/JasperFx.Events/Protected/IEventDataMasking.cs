using System;
using System.Linq.Expressions;

namespace JasperFx.Events.Protected;

/// <summary>
/// Fluent builder for applying data-protection masking to already-stored events — rewriting
/// protected information in place for GDPR-style erasure, without rewriting the stream.
/// </summary>
/// <remarks>
/// <para>
/// Lifted here because both Critter Stack event stores declared this interface member-for-member
/// identically in parallel namespaces (marten#5154). It sits beside
/// <see cref="StreamCompactingRequest{T}"/> for the same reason: the *shape* of the request is a
/// database-agnostic description of intent, while executing it is unavoidably store-specific — each
/// product resolves its own session, query surface and update path.
/// </para>
/// <para>
/// A store exposes this through its own advanced-operations surface (both current products spell it
/// <c>Advanced.ApplyEventDataMasking(Action&lt;IEventDataMasking&gt;, CancellationToken)</c>) and
/// supplies the implementation. Masking rules themselves are registered per event type on the store's
/// event options, not here.
/// </para>
/// </remarks>
public interface IEventDataMasking
{
    /// <summary>
    /// Isolate the event masking to a specific tenant if using multi-tenancy.
    /// </summary>
    IEventDataMasking ForTenant(string tenantId);

    /// <summary>
    /// Apply data protection masking to every event in this stream.
    /// </summary>
    IEventDataMasking IncludeStream(Guid streamId);

    /// <summary>
    /// Apply data protection masking to every event in this stream.
    /// </summary>
    IEventDataMasking IncludeStream(string streamKey);

    /// <summary>
    /// Apply data protection masking to the events within this stream that match the filter.
    /// </summary>
    IEventDataMasking IncludeStream(Guid streamId, Func<IEvent, bool> filter);

    /// <summary>
    /// Apply data protection masking to the events within this stream that match the filter.
    /// </summary>
    IEventDataMasking IncludeStream(string streamKey, Func<IEvent, bool> filter);

    /// <summary>
    /// Apply data protection masking to every event matching this criteria.
    /// </summary>
    IEventDataMasking IncludeEvents(Expression<Func<IEvent, bool>> filter);

    /// <summary>
    /// Add a header value to the metadata of any event masked as part of this batch. Applies only to
    /// event types that have a matching masking rule.
    /// </summary>
    IEventDataMasking AddHeader(string key, object value);
}
