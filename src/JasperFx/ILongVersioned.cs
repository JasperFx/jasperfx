#nullable enable
namespace JasperFx;

/// <summary>
///     Optionally implement this interface on your document types to opt into optimistic
///     concurrency with the version tracked on the <see cref="Version"/> property using a
///     64-bit numeric revision. Prefer this over <see cref="IRevisioned"/> for documents
///     projected from a multi-stream projection, where <see cref="Version"/> is the global
///     event sequence number and can exceed <see cref="int"/>.
/// </summary>
/// <remarks>
/// Sibling of <see cref="IRevisioned"/> (<c>int Version</c>, the Marten V8 signature, kept
/// as-is). Users pick whichever fits: <see cref="IRevisioned"/> for ordinary per-document
/// revision counters, <see cref="ILongVersioned"/> when the version is an event sequence
/// number. See <see href="https://github.com/JasperFx/jasperfx/issues/348">#348</see>;
/// part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public interface ILongVersioned
{
    /// <summary>
    /// The known version for this document.
    /// </summary>
    long Version { get; set; }
}
