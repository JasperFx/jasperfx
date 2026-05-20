namespace JasperFx.Events;

/// <summary>
/// Resolves the database table name backing a document, projection, or event-store table —
/// qualified (schema + table) or bare. A single "where does this document live" surface for
/// diagnostics, schema inspection, and projection-coordinator activity tags.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>Marten.IDocumentSchemaResolver</c> as the canonical, dialect-agnostic
/// contract. The implementation stays per-store. Polecat has no equivalent yet but will need
/// one; lifting the contract preemptively establishes a shared cross-store diagnostics surface
/// and saves a second-rewrite cost. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public interface IDocumentSchemaResolver
{
    /// <summary>
    ///     The schema name used to store the documents.
    /// </summary>
    string DatabaseSchemaName { get; }

    /// <summary>
    ///     The database schema name for event related tables. By default this
    ///     is the same schema as the document storage.
    /// </summary>
    string EventsSchemaName { get; }

    /// <summary>
    ///     Find the database name of the table backing <typeparamref name="TDocument"/>. Supports documents and projections.
    /// </summary>
    /// <typeparam name="TDocument">The document or projection to look up.</typeparam>
    /// <param name="qualified">
    ///     When true (default) the qualified name is returned (schema and table name).
    ///     Otherwise only the table name is returned.
    /// </param>
    /// <returns>The name of <typeparamref name="TDocument"/> in the database.</returns>
    string For<TDocument>(bool qualified = true);

    /// <summary>
    ///     Find the database name of the table backing <paramref name="documentType"/>. Supports documents and projections.
    /// </summary>
    /// <param name="documentType">The document type.</param>
    /// <param name="qualified">
    ///     When true (default) the qualified name is returned (schema and table name).
    ///     Otherwise only the table name is returned.
    /// </param>
    /// <returns>The name of <paramref name="documentType"/> in the database.</returns>
    string For(Type documentType, bool qualified = true);

    /// <summary>
    ///     Find the database name of the events table.
    /// </summary>
    /// <param name="qualified">
    ///     When true (default) the qualified name is returned (schema and table name).
    ///     Otherwise only the table name is returned.
    /// </param>
    /// <returns>The name of the events table in the database.</returns>
    string ForEvents(bool qualified = true);

    /// <summary>
    ///     Find the database name of the event streams table.
    /// </summary>
    /// <param name="qualified">
    ///     When true (default) the qualified name is returned (schema and table name).
    ///     Otherwise only the table name is returned.
    /// </param>
    /// <returns>The name of the event streams table in the database.</returns>
    string ForStreams(bool qualified = true);

    /// <summary>
    ///     Find the database name of the event progression table.
    /// </summary>
    /// <param name="qualified">
    ///     When true (default) the qualified name is returned (schema and table name).
    ///     Otherwise only the table name is returned.
    /// </param>
    /// <returns>The name of the event progression table in the database.</returns>
    string ForEventProgression(bool qualified = true);
}
