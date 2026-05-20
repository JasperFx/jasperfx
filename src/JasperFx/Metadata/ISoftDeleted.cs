namespace JasperFx.Metadata;

/// <summary>
///     Optionally implement this interface on your document types to opt into "soft delete"
///     mechanics, with the deletion information tracked directly on the documents.
/// </summary>
/// <remarks>
/// Lifted from the byte-equivalent <c>ISoftDeleted</c> interfaces in Marten
/// (<c>Marten.Metadata</c>) and Polecat (<c>Polecat.Metadata</c>). Part of the Critter Stack
/// 2026 dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// Each store type-forwards its old public name to this type.
/// </remarks>
public interface ISoftDeleted
{
    /// <summary>
    ///     Has the store marked this document as soft deleted?
    /// </summary>
    bool Deleted { get; set; }

    /// <summary>
    ///     When was this document marked as deleted?
    /// </summary>
    DateTimeOffset? DeletedAt { get; set; }
}
