namespace JasperFx.Events;

/// <summary>
///     Thrown when an append is refused because the stream has been archived. Archiving is
///     reversible bookkeeping rather than deletion, so the refusal names the way back:
///     <c>UnArchiveStream</c>, which every store implements.
/// </summary>
/// <remarks>
///     Lifted from the three unrelated per-store types that expressed this same refusal — Marten's
///     generic <c>InvalidStreamOperationException</c>, Polecat's <c>InvalidStreamException</c> and
///     Fisher's <c>ArchivedStreamException</c> — the way jasperfx#751 lifted the other stream
///     exceptions. Fisher's shape is the canonical one: named for the condition, carrying the id,
///     and saying what to do about it. Stores subclass or type-forward to this; a store whose
///     message diverged keeps its wording through the protected message-overriding constructor.
/// </remarks>
public class ArchivedStreamException : Exception
{
    public ArchivedStreamException(object id)
        : this(
            $"Event stream '{id}' is archived and cannot be appended to. Call UnArchiveStream to reopen it, or start a new stream.",
            id)
    {
    }

    /// <summary>
    ///     For store subclasses whose message diverges from the canonical one (and whose tests may
    ///     assert on that message).
    /// </summary>
    protected ArchivedStreamException(string message, object id) : base(message)
    {
        Id = id;
    }

    /// <summary>
    ///     The identity of the archived stream, as a <see cref="Guid" /> or a <see cref="string" />
    ///     depending on the store's stream identity.
    /// </summary>
    public object Id { get; }
}
