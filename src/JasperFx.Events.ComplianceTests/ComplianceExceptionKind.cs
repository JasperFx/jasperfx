namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The event store failure categories a shared suite asserts by <em>behaviour</em>, naming the
/// category rather than a concrete exception type. Resolved to a type per store through
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.ExceptionTypeFor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each member corresponds to one of the six event store exceptions lifted into
/// <c>JasperFx.Events</c>, which is what the fixture returns by default. The indirection exists
/// because a store may legitimately own its exception hierarchy and therefore cannot subclass the
/// lifted type.
/// </para>
/// <para>
/// Marten is the concrete example: it enforces a convention test —
/// <c>all_exceptions_should_derive_from_MartenException</c> — that every exception Marten throws
/// derives from <c>MartenException</c>. C# has single inheritance, so a Marten type cannot derive
/// from both <c>MartenException</c> and the lifted JasperFx type. Marten therefore keeps its own
/// six declarations in <c>Marten.Exceptions</c> and names them from its fixture. That is a
/// legitimate store design, not a compliance gap, so the suite asserts the behaviour and lets the
/// store name the type.
/// </para>
/// <para>
/// This is <em>not</em> a licence to loosen an assertion to "something threw". The suite still
/// requires the nominated type; only the naming of it moved to the fixture.
/// </para>
/// </remarks>
public enum ComplianceExceptionKind
{
    /// <summary>
    /// An event row names an event type the store cannot resolve.
    /// Defaults to <see cref="UnknownEventTypeException"/>.
    /// </summary>
    UnknownEventType,

    /// <summary>
    /// An operation addressed a stream that does not exist.
    /// Defaults to <see cref="NonExistentStreamException"/>.
    /// </summary>
    NonExistentStream,

    /// <summary>
    /// A stream was started with an identity that is already taken.
    /// Defaults to <see cref="ExistingStreamIdCollisionException"/>.
    /// </summary>
    ExistingStreamIdCollision,

    /// <summary>
    /// An event's body could not be deserialized, though its type resolved.
    /// Defaults to <see cref="EventDeserializationFailureException"/>.
    /// </summary>
    EventDeserializationFailure,

    /// <summary>
    /// An exclusive lock on a stream could not be taken.
    /// Defaults to <see cref="StreamLockedException"/>.
    /// </summary>
    StreamLocked,

    /// <summary>
    /// The default tenant was used in a store configured to forbid it.
    /// Defaults to <see cref="DefaultTenantUsageDisabledException"/>.
    /// </summary>
    DefaultTenantUsageDisabled
}
