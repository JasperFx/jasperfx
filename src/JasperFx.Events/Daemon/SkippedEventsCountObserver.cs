using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Populates <see cref="ShardState.SkippedEventsCount"/> on the HighWaterMark state when the
/// HighWaterAgent publishes a <see cref="ShardAction.Skipped"/> event. The shared
/// <see cref="ShardStateTracker.MarkSkippingAsync"/> helper supplies
/// <see cref="ShardState.Sequence"/> and <see cref="ShardState.PreviousGoodMark"/> but does
/// not compute the count, so it is filled here using the most-recent semantic
/// (gap size = <c>Sequence - PreviousGoodMark</c>).
/// </summary>
/// <remarks>
/// Lifted from the functionally-identical observers in both stores (Marten's
/// <c>SkippedEventsCountObserver</c> and Polecat's <c>SkippedEventsCountAugmenter</c>).
/// Marten's <c>ShardName != ShardState.HighWaterMark</c> guard is a strict superset — kept
/// here. It is harmless for Polecat, which only subscribes this observer for the HighWaterMark
/// tracker. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public sealed class SkippedEventsCountObserver : IObserver<ShardState>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(ShardState value)
    {
        if (value.Action != ShardAction.Skipped) return;
        if (value.ShardName != ShardState.HighWaterMark) return;
        if (value.SkippedEventsCount.HasValue) return;

        var skipped = value.Sequence - value.PreviousGoodMark;
        if (skipped > 0)
        {
            value.SkippedEventsCount = skipped;
        }
    }
}
