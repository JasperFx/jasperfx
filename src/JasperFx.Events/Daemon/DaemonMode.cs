namespace JasperFx.Events.Daemon;

public enum DaemonMode
{
    /// <summary>
    ///     The projection daemon is disabled in this application and
    ///     will not be started as part of the application
    /// </summary>
    Disabled,

    /// <summary>
    ///     The system will start up the complete projection daemon with the assumption
    ///     that this node is the only execution node. This is appropriate for single
    ///     node deployments and local development usage
    /// </summary>
    Solo,

    /// <summary>
    ///     Marten will ensure that the full async projection daemon will only execute on
    ///     one node at a time, with fail over to other nodes.
    /// </summary>
    HotCold,

    /// <summary>
    ///     Async projections and subscriptions are executed by an external system
    ///     (e.g. Wolverine's managed event-subscription distribution) rather than by the
    ///     event store's own hosted daemon coordination. The store itself hosts no
    ///     coordinator and starts no agents — the same runtime posture as <see cref="Disabled" /> —
    ///     but, unlike Disabled, the store must NOT warn that async projections will never
    ///     run: the external host is responsible for executing them. Set by integrations
    ///     (never overriding an explicit user choice), not by application code directly.
    ///     See wolverine#3290.
    /// </summary>
    ExternallyManaged
}
