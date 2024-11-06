#nullable enable
using System.Diagnostics;

namespace JasperFx.Events.Projections;

public interface ISubscriptionMetrics
{
    Activity? TrackExecution(EventRange page);
    Activity? TrackLoading(EventRequest request);
    void UpdateGap(long highWaterMark, long lastCeiling);
    void UpdateProcessed(long count);
    Activity? TrackGrouping(EventRange page);
}
