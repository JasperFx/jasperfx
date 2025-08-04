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
}
