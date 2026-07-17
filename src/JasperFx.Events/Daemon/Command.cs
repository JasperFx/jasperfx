using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class Command
{
    internal long HighWaterMark;
    internal long LastCommitted;

    internal CommandType Type;

    internal static Command Completed(long ceiling)
    {
        return new Command { LastCommitted = ceiling, Type = CommandType.RangeCompleted };
    }

    // #4721: signals that the off-consumer optimized rebuild has finished so the agent can
    // resume continuous operation. Carries no sequence — the trailing RangeCompleted commands
    // the rebuild already posted have advanced LastCommitted to the replay ceiling by the time
    // this is processed (single-reader FIFO channel).
    internal static Command ReplayCompleted()
    {
        return new Command { Type = CommandType.ReplayCompleted };
    }

    public static Command HighWaterMarkUpdated(long sequence)
    {
        return new Command { HighWaterMark = sequence, Type = CommandType.HighWater };
    }

    // jasperfx#525: a deferred-rebuild range was buffered up to `ceiling` without committing. Carries the
    // ceiling in LastCommitted purely to advance the buffered-ceiling back-pressure marker.
    internal static Command RangeBuffered(long ceiling)
    {
        return new Command { LastCommitted = ceiling, Type = CommandType.RangeBuffered };
    }

    public static Command Started(long highWater, long lastCommitted)
    {
        return new Command { HighWaterMark = highWater, LastCommitted = lastCommitted };
    }
}
