namespace JasperFx.Descriptors;

/// <summary>
/// Snapshot of a projection's current status, including all of its
/// shards. Returned as a list by the explorer's <c>GetProjectionStatusesAsync</c>
/// to populate the projections page; live updates flow over the existing
/// <c>ShardStatesChanged</c> event so monitoring tools can subscribe
/// without polling.
/// </summary>
/// <remarks>
/// <para>
/// <b>The inventory is projections, not shards</b> (jasperfx#818). One <see cref="ProjectionStatus"/>
/// per registered <em>projection</em> — the store's <c>Projections.All</c> — and subscriptions are not
/// in it, even though a subscription is a daemon shard with progress of its own. Marten and Polecat
/// already read it this way and Fisher is the store that changes. The reading was ruled rather than
/// counted: this is the page an operator opens to ask about read models, and a subscription has no
/// document behind it. Subscription progress stays reachable, non-generically, through
/// <c>IEventStore.RegisteredShardNames()</c> correlated against
/// <c>IEventDatabase.FetchProjectionLagAsync</c> — which is the pairing jasperfx#815 exists for.
/// </para>
/// <para>
/// <b>A projection with no async shards reports an empty shard list.</b> An Inline or Live projection
/// runs no daemon agent, so there is no shard and nothing to report progress for.
/// <see cref="Lifecycle"/> already says why the list is empty, so a store must not synthesise a
/// stand-in shard to fill it — see the warning on <see cref="ShardStatus.State"/>.
/// </para>
/// </remarks>
/// <param name="ProjectionName">Configured name of the projection.</param>
/// <param name="Lifecycle">String form of the projection's lifecycle (Inline / Async / Live).</param>
/// <param name="Shards">
/// Per-shard status records for this projection. Empty — never a synthesised placeholder — when the
/// projection runs no async shards.
/// </param>
public sealed record ProjectionStatus(
    string ProjectionName,
    string Lifecycle,
    IReadOnlyList<ShardStatus> Shards);

/// <summary>
/// Per-shard status snapshot inside a <see cref="ProjectionStatus"/>.
/// Tracks how far the shard has processed and surfaces any latched error
/// so operators can spot stuck shards from the explorer view.
/// </summary>
/// <remarks>
/// <para>
/// Every member below is documented as a <em>contract</em> rather than as a description, because
/// three stores independently inferred what these five fields meant and arrived at three answers
/// (jasperfx#818). <c>ProjectionStatusCompliance</c> pins them.
/// </para>
/// <para>
/// <b>Reading statuses must never change what is running.</b> This is a diagnostics read. A store
/// that resolves a daemon in order to answer must do so through a member that only observes —
/// <c>IProjectionCoordinator.AllDaemonsAsync()</c> — and never through one that is contractually
/// allowed to go looking and <em>start</em> one, which <c>DaemonForDatabase</c> is.
/// </para>
/// </remarks>
/// <param name="ShardName">
/// Compound shard name — projection name and group key, the <c>ShardName.Identity</c> form
/// (<c>Trip:All</c>), not the projection name alone.
/// </param>
/// <param name="State">
/// String form of the shard's runtime state, from the closed vocabulary in
/// <see cref="ShardStatusState"/>.
/// <para>
/// This is a fact about the <em>running daemon</em>, which no progression table knows. A store that
/// can reach a daemon reports what that daemon says; a store that cannot reports
/// <see cref="ShardStatusState.Unknown"/>, which means "there is no daemon here to ask" and is a
/// genuinely different operational situation from <see cref="ShardStatusState.Stopped"/>. A daemon is
/// unreachable in more cases than it looks: <c>DaemonMode.ExternallyManaged</c>, a monitoring console
/// running in another process, or a hand-built store. Answering <c>Stopped</c> there is not a partial
/// answer but a wrong one — indistinguishable from a daemon that really has stopped, and it is the
/// reading an operator acts on.
/// </para>
/// <para>
/// <b>Never a lifecycle.</b> A projection that runs no shards reports an empty
/// <see cref="ProjectionStatus.Shards"/> list; it does not get a synthesised shard carrying
/// <c>"Inline"</c> or <c>"Live"</c> in this slot. Doing so makes one field mean a daemon state on some
/// rows and a lifecycle on others, and <see cref="ProjectionStatus.Lifecycle"/> already carries it.
/// </para>
/// </param>
/// <param name="ProcessedSequence">
/// Highest event sequence this shard has consumed — the shard's own progression. Zero for a shard
/// that has never run.
/// </param>
/// <param name="EventStoreSequence">
/// Current head sequence of the underlying event store at the moment of the snapshot — the highest
/// sequence the store has <em>issued</em>, not the daemon's high-water mark.
/// <para>
/// The distinction is the whole point of the field. The gap between it and
/// <see cref="ProcessedSequence"/> is what a console renders as lag, and the high-water row records
/// where the daemon got to. A daemon that is not running leaves that row behind, so sourcing this
/// from it makes every shard on a stopped daemon look caught up — the exact opposite of what a
/// projections page is opened to find out.
/// </para>
/// </param>
/// <param name="Error">Latched error message when the shard is in a failed state; <see langword="null"/> otherwise.</param>
public sealed record ShardStatus(
    string ShardName,
    string State,
    long ProcessedSequence,
    long EventStoreSequence,
    string? Error);

/// <summary>
/// The closed vocabulary <see cref="ShardStatus.State"/> is drawn from (jasperfx#818).
/// </summary>
/// <remarks>
/// <para>
/// Three of the four are the names of <see cref="AgentStatus"/> members, which is what a store reports
/// when it can reach a daemon and ask. <see cref="Unknown"/> is the fourth and has no
/// <see cref="AgentStatus"/> counterpart on purpose: it is the answer for "there is no daemon here to
/// ask", which is not a state any agent is ever in.
/// </para>
/// <para>
/// Constants rather than an enum because <see cref="ShardStatus.State"/> is a string on a record that
/// serializes over a wire to monitoring consoles, and widening it to an enum would be a breaking
/// change for every consumer already reading the string. They exist so a store spells the vocabulary
/// rather than retyping it, and so the compliance suite has something to assert closure against.
/// </para>
/// </remarks>
public static class ShardStatusState
{
    /// <summary>
    /// The shard is running and consuming events — <see cref="AgentStatus.Running" />.
    /// </summary>
    public const string Running = nameof(AgentStatus.Running);

    /// <summary>
    /// The shard has been temporarily paused after a failure and will be restarted —
    /// <see cref="AgentStatus.Paused" />.
    /// </summary>
    public const string Paused = nameof(AgentStatus.Paused);

    /// <summary>
    /// A daemon was reached and reports this shard as stopped — <see cref="AgentStatus.Stopped" />.
    /// Not the answer for "no daemon was reachable"; that is <see cref="Unknown" />.
    /// </summary>
    public const string Stopped = nameof(AgentStatus.Stopped);

    /// <summary>
    /// No daemon was visible to this store, so its runtime state could not be read. Distinct from
    /// <see cref="Stopped" />, which is a daemon's answer rather than the absence of one.
    /// </summary>
    public const string Unknown = "Unknown";

    /// <summary>
    /// Every legal value of <see cref="ShardStatus.State" />.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Running, Paused, Stopped, Unknown];

    /// <summary>
    /// The <see cref="ShardStatus.State" /> string for an <see cref="AgentStatus" /> a daemon
    /// reported.
    /// </summary>
    public static string From(AgentStatus status) => status.ToString();
}
