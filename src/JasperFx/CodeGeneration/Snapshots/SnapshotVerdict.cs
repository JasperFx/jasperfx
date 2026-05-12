namespace JasperFx.CodeGeneration.Snapshots;

/// <summary>
///     Result of <see cref="SnapshotGate.Verify"/>. The three values describe the
///     three boot-time situations a consumer can be in regarding pre-generated
///     snapshot artifacts.
/// </summary>
public enum SnapshotVerdict
{
    /// <summary>
    ///     The live-computed fingerprint matches the persisted one. The consumer's
    ///     snapshot artifacts can be trusted; skip the live discovery + codegen path.
    /// </summary>
    Accept,

    /// <summary>
    ///     No persisted fingerprint exists (first boot, or the generated folder was
    ///     cleared). The consumer should run the live discovery + codegen path as
    ///     normal and persist a fingerprint for the next boot. Not an error — this
    ///     is the steady-state result on a fresh deployment.
    /// </summary>
    FirstBoot,

    /// <summary>
    ///     A persisted fingerprint exists but differs from the live-computed one.
    ///     The consumer must fall back to the live discovery + codegen path
    ///     (the snapshot artifacts cannot be trusted) and re-persist the
    ///     fingerprint at the end of boot. This is logged via
    ///     <see cref="SnapshotGate.SnapshotRejectedLogTemplate"/> at
    ///     <see cref="Microsoft.Extensions.Logging.LogLevel.Information"/>.
    /// </summary>
    /// <remarks>
    ///     Soft-fallback is the policy in <i>every</i> mode, including production.
    ///     A stale snapshot should degrade gracefully — the worst case is a
    ///     slightly-slower first boot, not a refusal to start. Hard-fail behaviour
    ///     was explicitly rejected during the jasperfx#243 design.
    /// </remarks>
    RejectAndRegenerate
}
