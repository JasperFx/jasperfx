#nullable enable
namespace JasperFx;

/// <summary>
///     Optionally implement this interface on your document
///     types to opt into optimistic concurrency with the version
///     being tracked on the Version property using numeric revision values
/// </summary>
public interface IRevisioned
{
    /// <summary>
    /// The known version for this document
    /// </summary>
    int Version { get; set; }
}
