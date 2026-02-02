using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public partial class SubscriptionAgent
{
    /// <summary>
    /// Optimized "catch up" execution of a projection or subscription meant
    /// for test automation
    /// </summary>
    /// <param name="highWaterMark"></param>
    /// <param name="state"></param>
    /// <param name="cancellation"></param>
    public async Task CatchUpAsync(long highWaterMark, ShardState state, CancellationToken cancellation)
    {
        if (state.Sequence == highWaterMark) return;

        var progression = state.Sequence;
        while (!cancellation.IsCancellationRequested && progression < highWaterMark)
        {
            var request = new EventRequest
            {
                HighWater = highWaterMark,
                BatchSize = Options.BatchSize,
                Floor = progression,
                ErrorOptions = ErrorOptions,
                Runtime = _runtime,
                Name = Name,
                Metrics = Metrics
            };

            var events = await _loader.LoadAsync(request, cancellation);
            await _execution.ProcessImmediatelyAsync(this, events, cancellation);

            progression = events.Ceiling;
        }

        if (cancellation.IsCancellationRequested && progression < highWaterMark)
        {
            throw new CatchUpException(progression, highWaterMark);
        }
    }
}

public class CatchUpException : Exception
{
    public CatchUpException(long progression, long highWaterMark) : base($"CatchUp timed out. Progression is {progression}, High Water Mark was {highWaterMark}")
    {
    }
}