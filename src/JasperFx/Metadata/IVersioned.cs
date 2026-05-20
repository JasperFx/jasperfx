namespace JasperFx.Metadata;

/// <summary>
///     Optionally implement this interface on your document types to opt into optimistic
///     concurrency with a <see cref="Guid"/> version tracked on the document.
/// </summary>
/// <remarks>
/// Lifted from the identical <c>IVersioned</c> interfaces in Marten (<c>Marten.Metadata</c>)
/// and Polecat (<c>Polecat.Metadata</c>), both <c>Guid Version</c>. Part of the Critter Stack
/// 2026 dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// Each store type-forwards its old public name to this type.
/// </remarks>
public interface IVersioned
{
    /// <summary>
    ///     The store's version for this document.
    /// </summary>
    Guid Version { get; set; }
}
