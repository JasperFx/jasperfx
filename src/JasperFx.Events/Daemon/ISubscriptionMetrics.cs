#nullable enable
using System.Diagnostics;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public interface ISubscriptionMetrics
{
    Activity? TrackExecution(EventRange page);
    Activity? TrackLoading(EventRequest request);
    void UpdateGap(long highWaterMark, long lastCeiling);
    void UpdateProcessed(long count);
    Activity? TrackGrouping(EventRange page);
    void IncrementSkips();

    /// <summary>marten#6003: the shard parked <paramref name="count" /> identities.</summary>
    void IncrementParked(int count)
    {
    }

    /// <summary>marten#6003: the shard unparked an identity. <paramref name="outcome" /> says how.</summary>
    void IncrementUnparked(string outcome)
    {
    }

    /// <summary>marten#6003: the shard held back <paramref name="count" /> events of parked identities.</summary>
    void IncrementHeldEvents(int count)
    {
    }

    /// <summary>marten#6003: how many identities the shard holds parked now.</summary>
    void UpdateParkedCount(int count)
    {
    }
}
