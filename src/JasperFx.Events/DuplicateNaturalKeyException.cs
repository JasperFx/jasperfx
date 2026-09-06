namespace JasperFx.Events;

/// <summary>
///     Thrown when a second event stream tries to claim a natural key that is already mapped to a
///     different live stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>Refusing is the contract</b> (jasperfx#764). Two stores disagreed: Fisher refuses the
///         second claimant, while Polecat's <c>MERGE</c>-based lookup write repoints the key at the
///         newcomer — the original stream is still there and simply becomes unreachable by the
///         identifier it was created with. A natural key exists to name one stream, so a second
///         claimant is a bug in the caller's key derivation rather than an instruction, and silently
///         losing the original mapping is the worse of the two failure modes because nothing reports
///         it. Refusing is now what <c>NaturalKeyCompliance</c> pins for every store.
///     </para>
///     <para>
///         Re-asserting the <em>same</em> mapping is not this: every event carrying the key rewrites
///         the row, and that is idempotent by design. Nor is <em>renaming</em> a key on its own
///         stream, which retires the superseded key and frees that identifier for someone else.
///     </para>
///     <para>
///         Lifted from Fisher's <c>Fisher.Exceptions.DuplicateNaturalKeyException</c> following the
///         jasperfx#751 pattern, with its message adopted verbatim as the canonical wording. The
///         <see cref="ExistingStreamId" /> / <see cref="ClaimingStreamId" /> pair is a nullable
///         superset for a store that detects the existing mapping before writing (and therefore has
///         both ids in hand) rather than inferring the conflict from a write that affected no rows.
///         Stores subclass or type-forward to this.
///     </para>
/// </remarks>
public class DuplicateNaturalKeyException : Exception
{
    public DuplicateNaturalKeyException(Type aggregateType, object key) : this(aggregateType, key, null, null)
    {
    }

    public DuplicateNaturalKeyException(Type aggregateType, object key, object? existingStreamId,
        object? claimingStreamId)
        : this(
            $"The natural key '{key}' is already mapped to a different stream for aggregate type "
            + $"'{aggregateType.Name}'. A natural key identifies one stream; if the mapping is meant "
            + "to move, delete the existing row first.", aggregateType, key, existingStreamId, claimingStreamId)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected DuplicateNaturalKeyException(string message, Type aggregateType, object key,
        object? existingStreamId, object? claimingStreamId) : base(message)
    {
        AggregateType = aggregateType;
        Key = key;
        ExistingStreamId = existingStreamId;
        ClaimingStreamId = claimingStreamId;
    }

    /// <summary>
    ///     The aggregate type whose natural key lookup was being written.
    /// </summary>
    public Type AggregateType { get; }

    /// <summary>
    ///     The natural key value that two streams claimed, already unwrapped from any strong-typed
    ///     wrapper by <see cref="NaturalKeyDefinition" />.
    /// </summary>
    public object Key { get; }

    /// <summary>
    ///     The stream the key was already mapped to, when the throw site knew it.
    /// </summary>
    public object? ExistingStreamId { get; }

    /// <summary>
    ///     The stream that tried to take the key, when the throw site knew it.
    /// </summary>
    public object? ClaimingStreamId { get; }
}
