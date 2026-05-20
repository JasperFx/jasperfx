namespace JasperFx;

/// <summary>
/// Controls how documents are deleted. Lifted from the byte-identical enum that lived
/// in Marten's <c>Marten.Schema.DeleteStyle</c> and Polecat's
/// <c>Polecat.Metadata.DeleteStyle</c>.
/// </summary>
/// <remarks>
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>). Each store
/// type-forwards its old public name to this type so downstream consumers keep compiling.
/// </remarks>
public enum DeleteStyle
{
    /// <summary>
    ///     Hard delete: physically removes the row from the database.
    /// </summary>
    Remove,

    /// <summary>
    ///     Soft delete: marks the row as deleted without removing it.
    /// </summary>
    SoftDelete
}
