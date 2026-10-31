namespace JasperFx.Events.Appending;

/// <summary>
///     What a store found when it looked for a stream's row, which is one fact rather than the three a
///     <c>(version, exists, archived)</c> tuple can spell. A missing archived stream cannot be written
///     down here, so the decision table has no row for it.
/// </summary>
public enum StreamRowState
{
    /// <summary>
    ///     Nobody looked. The zero value on purpose: a store that forgets a stream in its lookup hands back
    ///     <c>default</c>, and an append refuses that loudly where "missing" would have quietly restarted
    ///     the stream at version 1 (jasperfx#1051).
    /// </summary>
    NotRead,

    /// <summary>
    ///     The read ran and found no row.
    /// </summary>
    Missing,

    /// <summary>
    ///     The stream exists and can be appended to.
    /// </summary>
    Live,

    /// <summary>
    ///     The stream exists and is closed. Its id is still in use and its version is still its version.
    /// </summary>
    Archived
}

/// <summary>
///     One stream's row as a store read it, which is the only thing
///     <see cref="ClientSideAppendPlanner" /> knows about the database.
/// </summary>
public readonly record struct StreamRowSnapshot
{
    private StreamRowSnapshot(StreamRowState state, long version)
    {
        State = state;
        Version = version;
    }

    /// <summary>
    ///     What the read found.
    /// </summary>
    public StreamRowState State { get; }

    /// <summary>
    ///     The stream's version. Zero unless <see cref="State" /> is <see cref="StreamRowState.Live" /> or
    ///     <see cref="StreamRowState.Archived" />.
    /// </summary>
    public long Version { get; }

    /// <summary>
    ///     This store did not look, which is only legal for a <c>Start</c> whose stream row insert is
    ///     guarded by the database. See <see cref="StreamRowState.NotRead" />.
    /// </summary>
    public static StreamRowSnapshot NotRead { get; } = default;

    /// <summary>
    ///     The read ran and found no row.
    /// </summary>
    public static StreamRowSnapshot Missing { get; } = new(StreamRowState.Missing, 0);

    /// <summary>
    ///     The stream exists at <paramref name="version" />.
    /// </summary>
    public static StreamRowSnapshot Live(long version) => new(StreamRowState.Live, version);

    /// <summary>
    ///     The stream exists at <paramref name="version" /> and is archived.
    /// </summary>
    public static StreamRowSnapshot Archived(long version) => new(StreamRowState.Archived, version);

    /// <summary>
    ///     The shape both stores' reads already come back in: a null version when there is no row, and an
    ///     archived flag that is only meaningful when there is one.
    /// </summary>
    public static StreamRowSnapshot FromRow(long? version, bool isArchived)
    {
        if (version is not { } found) return Missing;

        return isArchived ? Archived(found) : Live(found);
    }
}
