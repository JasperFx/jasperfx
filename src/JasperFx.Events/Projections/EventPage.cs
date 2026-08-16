using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public class EventPage: List<IEvent>
{
    public EventPage(long floor)
    {
        Floor = floor;
    }

    public long Floor { get; }
    public long Ceiling { get; private set; }

    public long HighWaterMark { get; set; }

    /// <summary>
    /// The highest sequence a loader read but did not add to this page, or zero when none was reported.
    /// </summary>
    public long LastSkippedSequence { get; private set; }

    /// <summary>
    /// Tell the page about a row the loader read and deliberately did not keep — an unknown event type,
    /// or a deserialization failure the error options are configured to skip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loaders should call this for every skip, in addition to counting them for the
    /// <c>skippedEvents</c> argument of <see cref="CalculateCeiling" />. The count alone says how much of
    /// the batch was consumed; only the sequence says how far the query actually got, and
    /// <see cref="CalculateCeiling" /> needs both to place the ceiling on a page whose rows were all
    /// skipped. See <see href="https://github.com/JasperFx/jasperfx/issues/667" />.
    /// </para>
    /// <para>
    /// Out-of-order calls are harmless — the page keeps the maximum — so a loader that reads its rows in
    /// sequence order can call this as it goes without any ordering ceremony. A non-positive sequence is
    /// ignored rather than recorded, which is what makes the sentinel a store uses for "the sequence
    /// could not be determined" (Marten spells it <c>-1</c>) degrade to the fallback in
    /// <see cref="CalculateCeiling" /> instead of pinning the ceiling to the floor.
    /// </para>
    /// </remarks>
    public void RecordSkippedEvent(long sequence)
    {
        if (sequence > LastSkippedSequence)
        {
            LastSkippedSequence = sequence;
        }
    }

    /// <summary>
    /// Record a skip straight from the exception that caused it.
    /// </summary>
    /// <remarks>
    /// The overload a loader's <c>catch</c> block wants: <see cref="IEventFailureContext" /> already
    /// guarantees <see cref="IEventFailureContext.Sequence" />, and every skippable read failure in the
    /// Critter Stack stores implements it. Taking the sequence from there rather than making each loader
    /// dig it out per exception type is what keeps the sentinel handling in one place.
    /// </remarks>
    public void RecordSkippedEvent(IEventFailureContext failure)
    {
        RecordSkippedEvent(failure.Sequence);
    }

    /// <summary>
    /// Decide how far this page's consumer may advance durable progress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction that matters is whether the query <em>exhausted its range</em> or <em>stopped at
    /// its <c>LIMIT</c></em>. A page that did not fill its batch exhausted the range, so nothing is left
    /// between here and <paramref name="highWaterMark" /> and the ceiling may claim all of it. A page
    /// that filled its batch says nothing about the rows past the <c>LIMIT</c>, so the ceiling must stop
    /// at the last row actually observed.
    /// </para>
    /// <para>
    /// "Observed" rather than "kept" is the correction from jasperfx#667. A batch can be saturated and
    /// still leave the page empty, when every row in it was skipped — a contiguous span of a removed or
    /// renamed event type will do it. Reading that as "the range was exhausted" and claiming
    /// <paramref name="highWaterMark" /> writes durable progress past rows the query never returned,
    /// which is the one outcome this method exists to prevent. Skips are observations: they move the
    /// ceiling forward, so a poison span is stepped over exactly once rather than re-read forever, but
    /// they move it only as far as the loader actually got.
    /// </para>
    /// <para>
    /// A loader that reports skip counts but not skip sequences (via <see cref="RecordSkippedEvent" />)
    /// still lands on <paramref name="highWaterMark" /> for an all-skipped saturated page. That is the
    /// pre-jasperfx#667 behavior, kept deliberately: the page cannot invent a sequence it was never told,
    /// and stalling at the floor instead would re-read the same poison rows forever
    /// (<see href="https://github.com/JasperFx/marten/issues/4663" />). Adopting
    /// <see cref="RecordSkippedEvent" /> is what closes the gap for a given store.
    /// </para>
    /// </remarks>
    /// <param name="batchSize">The row limit the loader's query was issued with.</param>
    /// <param name="highWaterMark">The upper bound of the range the loader was asked for.</param>
    /// <param name="skippedEvents">How many rows the loader read and did not keep.</param>
    public void CalculateCeiling(int batchSize, long highWaterMark, int skippedEvents = 0)
    {
        var saturated = batchSize > 0 && Count + skippedEvents == batchSize;
        if (!saturated)
        {
            Ceiling = highWaterMark;
            return;
        }

        // Rows arrive in sequence order, so the last one observed is the greater of the last kept and
        // the last skipped -- either may be the final row under the LIMIT.
        var lastKept = Count > 0 ? this[Count - 1].Sequence : 0;
        var lastObserved = Math.Max(lastKept, LastSkippedSequence);

        Ceiling = lastObserved > 0 ? lastObserved : highWaterMark;
    }
}
