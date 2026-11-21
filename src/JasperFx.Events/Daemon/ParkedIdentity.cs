using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// One identity of an async projection that the daemon parked after it failed (marten#6003). The identity's
/// document stays at the last good state, which holds every event of the identity up to
/// <see cref="ResumeAfter" />, and the shard holds every later event for it until the identity is replayed or
/// discarded.
/// </summary>
/// <remarks>
/// A store keeps one row per (projection, version, shard key, tenant, identity) and writes it inside the projection
/// batch that moves the shard's progression past the failing event, through
/// <see cref="IProjectionBatch.ParkIdentity" />.
/// </remarks>
public sealed record ParkedIdentity
{
    /// <summary>The projection's name, <see cref="ShardName.Name" />.</summary>
    public required string ProjectionName { get; init; }

    /// <summary>The projection's version, <see cref="ShardName.Version" />. Blue/green versions park independently.</summary>
    public uint ProjectionVersion { get; init; } = 1;

    /// <summary>The shard key, <see cref="ShardName.ShardKey" />.</summary>
    public required string ShardKey { get; init; }

    /// <summary>The tenant of the identity's slice. Never null: the default tenant when the store isn't multi-tenanted.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The identity in its native type: a <see cref="Guid" />, <see cref="string" />, <see cref="int" />,
    /// <see cref="long" />, or the projection's strong-typed identifier.
    /// </summary>
    public required object Identity { get; init; }

    /// <summary>The canonical text of the unwrapped identity. See <see cref="ParkedIdentityText" />.</summary>
    public required string IdentityText { get; init; }

    /// <summary><c>guid</c>, <c>string</c>, <c>int</c> or <c>long</c>: the type of the unwrapped identity.</summary>
    public required string IdentityType { get; init; }

    /// <summary>
    /// The natural key of the identity's stream, as text (jasperfx#1273). A store fills it in when it reads the
    /// parked list in <see cref="IEventDatabase.FetchParkedIdentitiesAsync" />; it is never stored on the parked row.
    /// Null when the aggregate has no natural key, or the stream never got one.
    /// </summary>
    public string? NaturalKey { get; init; }

    /// <summary>The sequence of the event that failed.</summary>
    public long FirstFailingSequence { get; init; }

    /// <summary>The identity's document holds every one of its events up to and including this sequence.</summary>
    public long ResumeAfter { get; init; }

    /// <summary><see cref="ShardFailureCategory.ApplyEvent" /> or <see cref="ShardFailureCategory.EventSerialization" />.</summary>
    public ShardFailureCategory Category { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    /// <summary>The store's type alias of the failing event, when known.</summary>
    public string? EventTypeName { get; init; }

    /// <summary>The id of the failing event, when known.</summary>
    public Guid? EventId { get; init; }

    /// <summary>The failing event's stream, as text. Differs from the identity for a multi-stream projection.</summary>
    public string? FailingStream { get; init; }

    public DateTimeOffset ParkedAt { get; init; }

    /// <summary>How many replays of this identity have failed.</summary>
    public int ReplayAttempts { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public string? LastAttemptError { get; init; }

    /// <summary>
    /// An operator's request, set from any process with <see cref="IEventDatabase.RequestParkedActionAsync" />. The
    /// node that runs the shard picks it up on its heartbeat.
    /// </summary>
    public ParkedAction? RequestedAction { get; init; }

    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>The row's key.</summary>
    public ParkedIdentityKey Key => new(ProjectionName, ProjectionVersion, ShardKey, TenantId, IdentityText);
}

/// <summary>
/// The key of a <see cref="ParkedIdentity" /> row (marten#6003).
/// </summary>
public sealed record ParkedIdentityKey(
    string ProjectionName,
    uint ProjectionVersion,
    string ShardKey,
    string TenantId,
    string IdentityText);

/// <summary>
/// What an operator asked for on a parked identity (marten#6003).
/// </summary>
public enum ParkedAction
{
    /// <summary>
    /// Re-fold the identity and, if it succeeds, unpark it. If it fails again, it stays parked.
    /// </summary>
    Replay,

    /// <summary>
    /// Accept the loss: re-fold the identity, dropping and dead-lettering each event that still fails, apply the
    /// rest, and unpark it.
    /// </summary>
    Discard
}

/// <summary>
/// How a replay or discard of a parked identity ended (marten#6003).
/// </summary>
public enum ParkedReplayOutcome
{
    /// <summary>The identity was re-folded and unparked.</summary>
    Replayed,

    /// <summary>The identity was re-folded with its failing events dead-lettered, and unparked.</summary>
    Discarded,

    /// <summary>The re-fold failed again. The identity is still parked, with the new error on its row.</summary>
    FailedAgain,

    /// <summary>The identity was not parked, so there was nothing to do.</summary>
    NotParked
}

/// <summary>
/// The result of replaying or discarding one parked identity (marten#6003).
/// </summary>
/// <param name="Identity">The identity.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="BoundCeiling">The ceiling of the batch the re-fold committed with, or 0 when nothing committed.</param>
/// <param name="Exception">Why it failed again, for <see cref="ParkedReplayOutcome.FailedAgain" />.</param>
public sealed record ParkedReplayResult(
    object Identity,
    ParkedReplayOutcome Outcome,
    long BoundCeiling,
    Exception? Exception = null);

/// <summary>
/// Which parked identities a bulk replay takes (marten#6003).
/// </summary>
public sealed record ParkedSelection
{
    private ParkedSelection(int? oldest)
    {
        Oldest = oldest;
    }

    /// <summary>Every parked identity.</summary>
    public static ParkedSelection All { get; } = new((int?)null);

    /// <summary>The <paramref name="count" /> identities parked first.</summary>
    public static ParkedSelection OldestFirst(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return new ParkedSelection(count);
    }

    /// <summary>Null for every parked identity, otherwise how many of the oldest to take.</summary>
    public int? Oldest { get; }
}

/// <summary>
/// A query of the parked identities a store holds (marten#6003). Every filter is optional.
/// </summary>
public sealed record ParkedIdentityQuery
{
    public string? ProjectionName { get; init; }

    public uint? ProjectionVersion { get; init; }

    public string? ShardKey { get; init; }

    public string? TenantId { get; init; }

    /// <summary>The most rows to return, oldest first. The default is 100.</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>
/// How many identities one shard holds parked (marten#6003).
/// </summary>
public sealed record ParkedShardCount(
    string ProjectionName,
    uint ProjectionVersion,
    string ShardKey,
    string TenantId,
    int Count);

/// <summary>
/// A durable request for the node that runs a shard to replay or discard parked identities (marten#6003). It sets
/// <see cref="ParkedIdentity.RequestedAction" /> on every matching row.
/// </summary>
public sealed record ParkedActionRequest
{
    public required string ProjectionName { get; init; }

    public required ParkedAction Action { get; init; }

    /// <summary>Null for every version.</summary>
    public uint? ProjectionVersion { get; init; }

    /// <summary>Null for every tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>One identity's <see cref="ParkedIdentityText" />, or null for every parked identity.</summary>
    public string? IdentityText { get; init; }

    /// <summary>With no <see cref="IdentityText" />, take only this many of the oldest rows.</summary>
    public int? Oldest { get; init; }
}

/// <summary>
/// An event the store could not deserialize, reported on <see cref="EventPage.Unreadable" /> instead of throwing
/// when the shard parks unreadable events (marten#6003).
/// </summary>
public sealed record UnreadableEvent(
    long Sequence,
    Guid? StreamId,
    string? StreamKey,
    string? TenantId,
    string? EventTypeName,
    Exception Exception);

/// <summary>
/// The canonical text form of a parked identity (marten#6003), shared by every store so a row written by one
/// process reads the same in another: a <see cref="Guid" /> as <c>"D"</c>, numbers in the invariant culture, a
/// strong-typed identifier unwrapped to its simple value first.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "The identity is a projection's own identity type, preserved where the projection is registered.")]
public static class ParkedIdentityText
{
    /// <summary>Unwrap a strong-typed identifier, or return a raw identity as it is.</summary>
    public static object Unwrap(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity is Guid or string or int or long) return identity;

        if (StrongTypedIdResolver.TryResolve(identity.GetType(), out var info, StrongTypedIdStyle.AnyType))
        {
            return StrongTypedIdResolver.Unwrap(info, identity);
        }

        throw new ArgumentOutOfRangeException(nameof(identity),
            $"{identity.GetType().FullNameInCode()} is not an identity a parked row can hold.");
    }

    /// <summary>The canonical text of <paramref name="identity" />.</summary>
    public static string Format(object identity)
    {
        return Unwrap(identity) switch
        {
            Guid guid => guid.ToString("D"),
            string text => text,
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            var other => throw new ArgumentOutOfRangeException(nameof(identity),
                $"{other.GetType().FullNameInCode()} is not an identity a parked row can hold.")
        };
    }

    /// <summary><c>guid</c>, <c>string</c>, <c>int</c> or <c>long</c>.</summary>
    public static string TypeNameOf(object identity)
    {
        return Unwrap(identity) switch
        {
            Guid => "guid",
            string => "string",
            int => "int",
            long => "long",
            var other => throw new ArgumentOutOfRangeException(nameof(identity),
                $"{other.GetType().FullNameInCode()} is not an identity a parked row can hold.")
        };
    }

    /// <summary>
    /// Read an identity back in its native type: <paramref name="identityType" /> is the projection's identity
    /// type, which may be a strong-typed identifier.
    /// </summary>
    public static object Parse(string text, Type identityType)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(identityType);

        if (identityType == typeof(Guid)) return Guid.Parse(text);
        if (identityType == typeof(string)) return text;
        if (identityType == typeof(int)) return int.Parse(text, CultureInfo.InvariantCulture);
        if (identityType == typeof(long)) return long.Parse(text, CultureInfo.InvariantCulture);

        if (StrongTypedIdResolver.TryResolve(identityType, out var info, StrongTypedIdStyle.AnyType))
        {
            return StrongTypedIdResolver.Wrap(info, Parse(text, info.SimpleType));
        }

        throw new ArgumentOutOfRangeException(nameof(identityType),
            $"{identityType.FullNameInCode()} is not an identity a parked row can hold.");
    }

    /// <summary>Read an identity back as <typeparamref name="TId" />.</summary>
    public static TId Parse<TId>(string text) where TId : notnull => (TId)Parse(text, typeof(TId));
}

/// <summary>
/// A shard paused instead of parking (marten#6003): either a batch would have parked more identities than
/// <see cref="ErrorHandlingOptions.MaxParkedIdentitiesPerShard" /> allows, or it would have parked so many of its
/// identities at once (<see cref="ErrorHandlingOptions.ParkedTripMinimum" />) that the failure is the projection's,
/// not the data's. Reported as <see cref="ShardFailureCategory.ParkedLimitReached" />.
/// </summary>
public class ParkedLimitReachedException : Exception, IEventFailureContext
{
    public ParkedLimitReachedException(ShardName shard, string message, ApplyEventException? firstFailure)
        : base($"Shard '{shard.Identity}' paused instead of parking: {message}", firstFailure)
    {
        Shard = shard;
        FirstFailure = firstFailure;
    }

    public ShardName Shard { get; }

    /// <summary>One of the failures that would have been parked, when it was an apply error.</summary>
    public ApplyEventException? FirstFailure { get; }

    ShardFailureCategory IEventFailureContext.Category => ShardFailureCategory.ParkedLimitReached;

    long IEventFailureContext.Sequence => FirstFailure?.Event.Sequence ?? 0;

    string? IEventFailureContext.EventTypeName => FirstFailure?.Event.EventTypeName;

    Guid? IEventFailureContext.EventId => FirstFailure?.Event.Id;

    Guid? IEventFailureContext.StreamId =>
        FirstFailure == null || FirstFailure.Event.StreamId == Guid.Empty ? null : FirstFailure.Event.StreamId;

    string? IEventFailureContext.StreamKey =>
        string.IsNullOrEmpty(FirstFailure?.Event.StreamKey) ? null : FirstFailure.Event.StreamKey;

    string? IEventFailureContext.TenantId => FirstFailure?.Event.TenantId;

    long? IEventFailureContext.Version => FirstFailure?.Event.Version;
}
