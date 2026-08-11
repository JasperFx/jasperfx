using System;
using System.Collections.Generic;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Store-neutral description of the document store configuration a document compliance suite needs.
/// A suite fills one of these in; the fixture replays it against its own store.
/// </summary>
/// <remarks>
/// Far thinner than <see cref="ComplianceStoreConfig" />, and that is the point rather than an
/// oversight. The document contract (jasperfx#647) is seven operations, so a suite needs nothing but
/// a schema to live in and the document types it will exercise — there is no registrar interface
/// here because there is nothing store-specific left to register.
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
}
