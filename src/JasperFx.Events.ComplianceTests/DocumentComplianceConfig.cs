using System;
using System.Collections.Generic;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Store-neutral description of the document store configuration a document compliance suite needs.
/// A suite fills one of these in; the fixture replays it against its own store.
/// </summary>
/// <remarks>
/// Far thinner than <see cref="ComplianceStoreConfig" />, and that is the point rather than an
/// oversight. The document contract (jasperfx#647) is eight operations, so a suite needs nothing but
/// a schema to live in, the document types it will exercise, and the strong-typed identifiers those
/// documents are keyed by. Still no registrar interface: unlike the event side, every one of those is
/// expressible as a <see cref="Type" /> the fixture replays against its own options.
/// </remarks>
public sealed class DocumentComplianceConfig
{
    /// <summary>
    /// Optional schema/namespace override. When null the fixture picks its own.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The document types the suite will store, query and delete. Stores that create document
    /// storage on demand may ignore this; stores that need to be told up front use it.
    /// </summary>
    public List<Type> DocumentTypes { get; } = new();

    public DocumentComplianceConfig AddDocumentType<T>() where T : notnull
    {
        DocumentTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Strong-typed identifier wrappers used as document identities in this configuration.
    /// </summary>
    /// <remarks>
    /// Needed by <see cref="Documents.IDocumentReadOperations.LoadAsync{T}(object,System.Threading.CancellationToken)" />,
    /// which is the only member of the document contract whose behavior depends on store
    /// configuration the contract itself does not carry. Every Critter Stack store spells the
    /// registration <c>StoreOptions.RegisterValueType(Type)</c>, so a fixture replays this with a
    /// loop; stores that discover value types automatically can ignore it.
    /// </remarks>
    public List<Type> ValueTypes { get; } = new();

    public DocumentComplianceConfig RegisterValueType<TValue>() where TValue : notnull
    {
        ValueTypes.Add(typeof(TValue));
        return this;
    }
}
