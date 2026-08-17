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
    /// The sequence of the last row the loader actually read, whether or not it survived into this
    /// page. Set it when a loader skips events, so a page that ends up empty can still report how
    /// far its query really got.
    /// </summary>
    /// <remarks>
    /// The page itself only ever sees events that deserialized, so it cannot work this out alone.
    /// A loader that does not set it keeps the previous behaviour exactly.
    /// </remarks>
    public long? LastObservedSequence { get; set; }

    public void CalculateCeiling(int batchSize, long highWaterMark, int skippedEvents = 0)
    {
        if ((Count + skippedEvents) != batchSize)
        {
            // The query did not fill the batch, so it exhausted everything up to the bound it was
            // given and the caller's high water mark is the honest ceiling.
            Ceiling = highWaterMark;
            return;
        }

        if (Count > 0)
        {
            Ceiling = this.Last().Sequence;
            return;
        }

        // Every row in a full batch was skipped, so there is no last event to read a sequence
        // from. The batch was still saturated: the LIMIT stopped the query, and rows beyond it
        // inside the same range were never looked at. Claiming highWaterMark here writes durable
        // progress past events nobody read, which is the same over-claim the branch above exists
        // to prevent. See https://github.com/JasperFx/jasperfx/issues/667
        //
        // The last row the loader saw is the correct ceiling: it is as far as the query got, and
        // advancing to it still steps over the poison rows, so a shard cannot get stuck re-reading
        // them. That liveness is why the fallback below exists at all. See
        // https://github.com/JasperFx/marten/issues/4663
        //
        // A loader that does not report LastObservedSequence still gets highWaterMark, so this
        // changes nothing for callers that have not opted in.
        Ceiling = LastObservedSequence ?? highWaterMark;
    }
}
