namespace JasperFx;

/// <summary>
///     Thrown when a LINQ expression cannot be translated into something the store can answer
///     correctly.
/// </summary>
/// <remarks>
///     <para>
///         Lifted from the three per-store exceptions of the same name (jasperfx#795), following the
///         jasperfx#756 pattern: superset constructors, plus a protected message-overriding
///         constructor so a store whose wording diverged can subclass without breaking tests that pin
///         its message.
///     </para>
///     <para>
///         <b>The refusal it carries is a correctness guarantee, not a convenience.</b> Every store
///         throws it where the alternative is a query that returns plausible but wrong rows — Fisher
///         on an ordering or range comparison over a date member whose stored JSON form does not sort
///         as text, Polecat on streaming a client-side-fallback projection as raw JSON. A caller that
///         catches this has been told "I cannot answer this correctly", which is categorically
///         different from an empty result.
///     </para>
///     <para>
///         ⚠️ <b>It lives in <c>JasperFx</c> rather than <c>JasperFx.Events</c> deliberately.</b> This
///         is the document/LINQ surface, and a document-only store has no reason to take the events
///         package to name the exception its query provider throws.
///     </para>
///     <para>
///         <b>Marten is deliberately not expected to adopt this yet.</b> marten#5346 ruled that Marten
///         keeps its own hierarchy for now — <c>all_exceptions_should_derive_from_MartenException</c>
///         requires every Marten exception to derive from <c>MartenException</c>, C# has single
///         inheritance, and the maintainer deferred that break to Marten 10. So this is a two-store
///         convergence in the near term, with Marten joining later. Until it does, a store-agnostic
///         consumer that must catch all three should use
///         <c>EventStoreComplianceFixture.ExceptionTypeFor</c>'s approach — nominate the type rather
///         than assume it.
///     </para>
/// </remarks>
public class BadLinqExpressionException : Exception
{
    public BadLinqExpressionException(string message) : base(message)
    {
    }

    public BadLinqExpressionException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>
    ///     For store subclasses that carry additional context, or whose message is composed rather
    ///     than passed in.
    /// </summary>
    protected BadLinqExpressionException(string message, Exception? innerException, string? expression)
        : base(message, innerException)
    {
        Expression = expression;
    }

    /// <summary>
    ///     The offending expression as text, when the throw site had it.
    /// </summary>
    /// <remarks>
    ///     Nullable because none of the three stores capture it today; it is here so that a store that
    ///     starts to can do it without another lift. A consumer must not depend on it being set.
    /// </remarks>
    public string? Expression { get; }
}
