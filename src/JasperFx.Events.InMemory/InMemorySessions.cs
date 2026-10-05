using JasperFx.Events.Documents;

namespace JasperFx.Events.InMemory;

/// <summary>
/// A read-only session on the in-memory prototyping store (jasperfx#964). The query-session type the
/// shared projection and aggregation runtime is closed over.
/// </summary>
public interface IInMemoryQuerySession : IDocumentReadOperations;

/// <summary>
/// A writable session on the in-memory prototyping store (jasperfx#964). The operations type the shared
/// projection and aggregation runtime is closed over; <see cref="InMemoryDocumentSession"/> implements it.
/// </summary>
/// <remarks>
/// Interfaces rather than the concrete session as the generic arguments, the same shape as Marten's and
/// Fisher's <c>IQuerySession</c> / <c>IDocumentSession</c>: closing the shared bases over one concrete
/// type for both makes overloads ambiguous, and the source generator binds the session by type name.
/// </remarks>
public interface IInMemoryDocumentSession : IInMemoryQuerySession, IDocumentSessionOperations, IStorageOperations;
