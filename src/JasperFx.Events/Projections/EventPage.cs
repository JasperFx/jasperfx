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

    public void CalculateCeiling(int batchSize, long highWaterMark, int skippedEvents = 0)
    {
        // Count == 0 happens when every event in a full batch was skipped (e.g. error
        // handling is configured to skip serialization/application/unknown event errors).
        // There is no last event to read a sequence from, so fall back to the high water
        // mark and let the daemon make progress rather than throwing on an empty page. See
        // https://github.com/JasperFx/marten/issues/4663
        Ceiling = (Count + skippedEvents) == batchSize && Count > 0
            ? this.Last().Sequence
            : highWaterMark;
    }
}
