namespace JasperFx.Events.Descriptors;

/// <summary>
/// Diagnostic mirror of <see cref="JasperFx.Events.Daemon.ErrorHandlingOptions"/>.
/// Carries the configured async-daemon error-skipping policy as a plain wire
/// shape so monitoring tools (e.g. CritterWatch) can render the policy and
/// decide whether to surface per-event dead-letter affordances vs. a
/// "shard halts on error" indicator. See JasperFx/ProductSupport#3.
/// </summary>
/// <remarks>
/// <para>
/// All three flags mirror the daemon's runtime behaviour: when a flag is
/// <see langword="true"/> the daemon skips the offending event (routing
/// failures to the Wolverine DLQ where applicable) and keeps the shard
/// advancing; when <see langword="false"/> the daemon stops the shard on
/// that class of error and the operator has to fix and restart.
/// </para>
/// <para>
/// Surfaced once per store via <see cref="EventStoreUsage.ProjectionErrors"/>
/// (the normal-run config) and <see cref="EventStoreUsage.ProjectionRebuildErrors"/>
/// (the rebuild-mode config). Marten and Polecat both expose the same
/// <c>StoreOptions.Projections.Errors</c> / <c>.RebuildErrors</c> split, so the
/// descriptor pair maps one-to-one onto the store config.
/// </para>
/// </remarks>
public class ProjectionErrorHandlingDescriptor
{
    /// <summary>
    /// Should the daemon skip "poison pill" events that fail in user projection
    /// code? Mirror of <see cref="JasperFx.Events.Daemon.ErrorHandlingOptions.SkipApplyErrors"/>.
    /// </summary>
    /// <remarks>
    /// JasperFx.Events 2.0 default for normal-run is <see langword="true"/>;
    /// the rebuild-mode default is <see langword="false"/>.
    /// </remarks>
    public bool SkipApplyErrors { get; set; }

    /// <summary>
    /// Should the daemon skip events whose type can't be resolved when fetched?
    /// Mirror of <see cref="JasperFx.Events.Daemon.ErrorHandlingOptions.SkipUnknownEvents"/>.
    /// </summary>
    public bool SkipUnknownEvents { get; set; }

    /// <summary>
    /// Should the daemon skip events that experience serialization errors?
    /// Mirror of <see cref="JasperFx.Events.Daemon.ErrorHandlingOptions.SkipSerializationErrors"/>.
    /// </summary>
    public bool SkipSerializationErrors { get; set; }
}
