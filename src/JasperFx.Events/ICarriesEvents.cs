namespace JasperFx.Events;

/// <summary>
/// Marks a type whose values carry events to append, where the type itself doesn't name them:
/// a stream start, an append batch. The JasperFx.Events source generator reads the event types
/// from the code that builds the value and records them in an <see cref="EmittedEventsAttribute" />
/// manifest. The interface changes no behavior.
/// </summary>
/// <remarks>
/// The generator treats a member of a marked type as event-carrying when its parameter is typed
/// <c>object</c> or as a collection of <c>object</c> — so a stream id typed <c>Guid</c> or
/// <c>string</c> beside the events is never mistaken for one. See jasperfx#990.
/// </remarks>
public interface ICarriesEvents;
