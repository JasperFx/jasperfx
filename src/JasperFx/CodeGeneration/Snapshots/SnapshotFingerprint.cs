using System.Text.Json.Serialization;

namespace JasperFx.CodeGeneration.Snapshots;

/// <summary>
///     Invalidation record persisted alongside pre-generated code by codegen consumers
///     (Marten, Wolverine, Polecat, etc.). At boot, consumers compare a live-computed
///     fingerprint against the persisted one via <see cref="SnapshotGate.Verify"/>; a
///     mismatch triggers a soft fallback to the live discovery + codegen path.
/// </summary>
/// <param name="ProductName">
///     Short, stable identifier for the consuming product — <c>"marten"</c>,
///     <c>"wolverine"</c>, <c>"polecat"</c>, etc. Used in the rejection log signature
///     so operators can tell which product invalidated.
/// </param>
/// <param name="ProductVersion">
///     SemVer of the consuming product at the time the snapshot was written.
///     Mismatch with the running version invalidates the snapshot — product
///     authors may have changed how the snapshot is interpreted between versions.
/// </param>
/// <param name="JasperFxVersion">
///     SemVer of JasperFx at the time the snapshot was written. Mismatch with the
///     running version invalidates — codegen output shape may have shifted.
/// </param>
/// <param name="ConfigHash">
///     Deterministic hash (typically SHA-256, via <see cref="SnapshotGate.ComputeHash"/>)
///     of consumer-specific config inputs. Each consumer decides what goes in: registered
///     types, projection registrations, serializer choice, transport URIs, etc. The hash
///     is opaque to JasperFx — we only compare for equality.
/// </param>
/// <param name="SchemaVersion">
///     Versioning of the <see cref="SnapshotFingerprint"/> shape itself. Bumped if this
///     record's fields ever change. Mismatch is treated identically to any other
///     mismatch — <see cref="SnapshotVerdict.RejectAndRegenerate"/>. Defaults to
///     <see cref="SnapshotGate.CurrentSchemaVersion"/>.
/// </param>
/// <remarks>
///     This record is the *entire* shared shape JasperFx requires across consumers in
///     Phase 1 of jasperfx#243. Consumers own their snapshot-artifact format completely;
///     JasperFx supplies only invalidation + the log signature.
/// </remarks>
public sealed record SnapshotFingerprint(
    string ProductName,
    string ProductVersion,
    string JasperFxVersion,
    string ConfigHash,
    int SchemaVersion = SnapshotGate.CurrentSchemaVersion)
{
    /// <summary>
    ///     Source-generation-friendly parameterless constructor. Required for
    ///     <c>System.Text.Json</c> deserialisation on consumers that use a
    ///     <see cref="JsonSerializerContext"/>; the positional record syntax
    ///     above is the canonical authoring form.
    /// </summary>
    [JsonConstructor]
    public SnapshotFingerprint() : this("", "", "", "", SnapshotGate.CurrentSchemaVersion)
    {
    }
}
