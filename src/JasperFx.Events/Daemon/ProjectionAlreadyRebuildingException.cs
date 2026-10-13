namespace JasperFx.Events.Daemon;

/// <summary>
///     A rebuild was requested for a projection (or one tenant of it) that this daemon is already rebuilding.
///     jasperfx#1009: the second rebuild fails fast instead of stopping agents and tearing down state under the
///     first one. Wait for the running rebuild, or check <see cref="IProjectionDaemon.IsRebuilding" /> first.
/// </summary>
public class ProjectionAlreadyRebuildingException : InvalidOperationException
{
    public ProjectionAlreadyRebuildingException(string projectionName, string? tenantId) : base(tenantId == null
        ? $"Projection '{projectionName}' is already being rebuilt on this daemon. Wait for that rebuild to finish before starting another."
        : $"Projection '{projectionName}' for tenant '{tenantId}' is already being rebuilt on this daemon (for that tenant or for every tenant). Wait for that rebuild to finish before starting another.")
    {
        ProjectionName = projectionName;
        TenantId = tenantId;
    }

    /// <summary>The projection whose rebuild was refused.</summary>
    public string ProjectionName { get; }

    /// <summary>The tenant the refused rebuild was for, or null for a rebuild of every tenant.</summary>
    public string? TenantId { get; }
}
