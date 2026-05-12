using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JasperFx.CodeGeneration.Snapshots;

/// <summary>
///     Phase 1 of jasperfx#243. The minimum shared surface across codegen consumers
///     (Marten, Wolverine, Polecat, etc.) for pre-generated-code invalidation:
///     a fingerprint persistence format, a verdict policy, and a stable log signature.
/// </summary>
/// <remarks>
///     <para>
///     What this is NOT: an opinion on artifact format, file layout beyond the
///     fingerprint itself, or how a consumer enumerates / serialises its pre-computed
///     boot state. Consumers own those calls completely. Phase 3 of jasperfx#243 may
///     extract a shared <c>ISnapshotArtifact</c> if both Marten and Wolverine end up
///     wanting one, but Phase 1 does not commit to that.
///     </para>
///     <para>
///     <b>Soft fallback in every mode.</b> A persisted fingerprint that disagrees
///     with the live one triggers <see cref="SnapshotVerdict.RejectAndRegenerate"/>;
///     the consumer falls back to live codegen and re-persists. Hard-fail behaviour
///     was rejected during the design (a stale snapshot in production should
///     degrade gracefully, not refuse to boot).
///     </para>
///     <para>
///     <b>The log template is a public stability promise.</b>
///     <see cref="SnapshotRejectedLogTemplate"/> is what operators grep for in logs
///     to recognise the "snapshot rejected, regenerating" path. Stable across
///     JasperFx 2.x minor versions.
///     </para>
/// </remarks>
public static class SnapshotGate
{
    /// <summary>
    ///     Canonical filename for the persisted <see cref="SnapshotFingerprint"/>
    ///     within a consumer's generated-code folder. One per
    ///     <see cref="ICodeFileCollection"/> export directory.
    /// </summary>
    public const string FingerprintFileName = "fingerprint.json";

    /// <summary>
    ///     Current <see cref="SnapshotFingerprint.SchemaVersion"/>. Bumped if the
    ///     record's shape ever changes; mismatched persisted values are treated as
    ///     <see cref="SnapshotVerdict.RejectAndRegenerate"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     Structured-log message template emitted at
    ///     <see cref="Microsoft.Extensions.Logging.LogLevel.Information"/> when a
    ///     consumer's snapshot is rejected. Operators grep for this exact prefix
    ///     when diagnosing slow first-boot-after-deploy. The template parameters
    ///     are <c>{ProductName}</c>, <c>{Namespace}</c>, <c>{Reason}</c> — in
    ///     that order.
    /// </summary>
    public const string SnapshotRejectedLogTemplate =
        "JasperFx.Codegen: snapshot rejected ({ProductName}/{Namespace}), regenerating. Reason: {Reason}";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Read the persisted fingerprint from a generated-code folder. Returns
    ///     <see langword="null"/> if no fingerprint exists (first boot, cleared
    ///     folder), or if the file is unreadable / malformed (treated as
    ///     "no usable persisted fingerprint" — caller will get
    ///     <see cref="SnapshotVerdict.FirstBoot"/> from <see cref="Verify"/>).
    /// </summary>
    /// <param name="generatedFolder">
    ///     Absolute path to the consumer's generated-code folder
    ///     (the <c>Internal/Generated</c>-style directory). Must exist; this method
    ///     does not create it.
    /// </param>
    public static SnapshotFingerprint? Read(string generatedFolder)
    {
        if (string.IsNullOrWhiteSpace(generatedFolder)) return null;

        var path = Path.Combine(generatedFolder, FingerprintFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SnapshotFingerprint>(json, _serializerOptions);
        }
        catch (Exception)
        {
            // Corrupted / unreadable fingerprint is indistinguishable from "no
            // fingerprint" for caller purposes — both result in regeneration on
            // the next Verify call. Don't surface the read failure as an
            // exception; the soft-fallback policy says boot continues.
            return null;
        }
    }

    /// <summary>
    ///     Persist a fingerprint to the generated-code folder. Creates the folder
    ///     if it doesn't exist. Overwrites any prior fingerprint atomically (writes
    ///     to a temp file then moves into place).
    /// </summary>
    public static void Write(string generatedFolder, SnapshotFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(generatedFolder))
            throw new ArgumentException("Generated folder path is required.", nameof(generatedFolder));
        ArgumentNullException.ThrowIfNull(fingerprint);

        Directory.CreateDirectory(generatedFolder);
        var finalPath = Path.Combine(generatedFolder, FingerprintFileName);
        var tempPath = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(fingerprint, _serializerOptions);
        File.WriteAllText(tempPath, json);

        // Atomic move so a partial write never leaves a corrupted fingerprint
        // visible to a concurrently-booting process.
        File.Move(tempPath, finalPath, overwrite: true);
    }

    /// <summary>
    ///     Compare a live-computed fingerprint against a persisted one and return
    ///     the verdict that drives the consumer's boot path.
    /// </summary>
    /// <param name="live">The freshly-computed fingerprint from the live boot path. Must not be null.</param>
    /// <param name="persisted">
    ///     The fingerprint read via <see cref="Read"/>, or <see langword="null"/>
    ///     if no fingerprint existed.
    /// </param>
    /// <returns>
    ///     <see cref="SnapshotVerdict.FirstBoot"/> if <paramref name="persisted"/> is null;
    ///     <see cref="SnapshotVerdict.Accept"/> if every field matches;
    ///     <see cref="SnapshotVerdict.RejectAndRegenerate"/> otherwise.
    /// </returns>
    public static SnapshotVerdict Verify(SnapshotFingerprint live, SnapshotFingerprint? persisted)
    {
        ArgumentNullException.ThrowIfNull(live);

        if (persisted is null) return SnapshotVerdict.FirstBoot;

        // Record equality is by-value across every field — exactly what we want.
        return live == persisted
            ? SnapshotVerdict.Accept
            : SnapshotVerdict.RejectAndRegenerate;
    }

    /// <summary>
    ///     Compute a SHA-256 hash of a canonical-form string and return it as a
    ///     lowercase hex digest. Convenience helper for <see cref="SnapshotFingerprint.ConfigHash"/>.
    ///     Consumers are free to use any deterministic hash function they prefer —
    ///     JasperFx only compares the resulting strings for equality.
    /// </summary>
    /// <param name="canonicalRepresentation">
    ///     A deterministic string representation of the consumer's config inputs.
    ///     The hash is only as stable as the canonicalisation; consumers must sort
    ///     collections, normalise whitespace, etc. before calling this.
    /// </param>
    public static string ComputeHash(string canonicalRepresentation)
    {
        ArgumentNullException.ThrowIfNull(canonicalRepresentation);

        var bytes = Encoding.UTF8.GetBytes(canonicalRepresentation);
        var hash = SHA256.HashData(bytes);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
