using JasperFx.Events.Tags;

namespace JasperFx.Events;

/// <summary>
/// Thrown when a Dynamic Consistency Boundary (DCB) check fails — new events matching the
/// tag query were appended after the boundary was established.
/// </summary>
/// <remarks>
/// Lifted from the conceptually-shared exception that lived in both Marten
/// (<c>Marten.Events.Dcb.DcbConcurrencyException : MartenException</c>) and Polecat
/// (<c>Polecat.Events.Dcb.DcbConcurrencyException : JasperFx.ConcurrencyException</c>).
/// Both carried the identical message, the same <c>(EventTagQuery, long)</c> ctor, and the
/// same <see cref="Query"/> / <see cref="LastSeenSequence"/> properties, and both already
/// depend on the shared <see cref="EventTagQuery"/>. Canonical home is JasperFx.Events,
/// based on <see cref="ConcurrencyException"/> (Polecat's shape). Part of the Critter Stack
/// 2026 dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public class DcbConcurrencyException : ConcurrencyException
{
    public DcbConcurrencyException(EventTagQuery query, long lastSeenSequence)
        : base(
            $"DCB consistency violation: new events matching the tag query were appended after sequence {lastSeenSequence}")
    {
        Query = query;
        LastSeenSequence = lastSeenSequence;
    }

    public EventTagQuery Query { get; }
    public long LastSeenSequence { get; }
}
