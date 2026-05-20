namespace JasperFx.Metadata;

/// <summary>
///     Optionally implement this interface to add correlation/causation tracking to your
///     document type, with the tracking information copied from the session onto the document
///     on save.
/// </summary>
/// <remarks>
/// Lifted from the <c>ITracked</c> interfaces in Marten (<c>Marten.Metadata</c>) and Polecat
/// (<c>Polecat.Metadata</c>). Marten's non-nullable <c>string</c> shape is canonical here;
/// Polecat declared the members nullable — it keeps its own annotations on the concrete
/// document classes when consuming this. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>). Each store
/// type-forwards its old public name to this type.
/// </remarks>
public interface ITracked
{
    /// <summary>
    ///     Correlation id for the last system activity to edit this document.
    /// </summary>
    string CorrelationId { get; set; }

    /// <summary>
    ///     Causation id for the last system activity to edit this document.
    /// </summary>
    string CausationId { get; set; }

    /// <summary>
    ///     The user who last modified this document.
    /// </summary>
    string LastModifiedBy { get; set; }
}
