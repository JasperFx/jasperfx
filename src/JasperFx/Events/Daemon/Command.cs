using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class Command
{
    internal long HighWaterMark;
    internal long LastCommitted;
    internal EventRange Range;

    internal CommandType Type;

    internal static Command Completed(long ceiling)
    {
        return new Command { LastCommitted = ceiling, Type = CommandType.RangeCompleted };
    }

    public static Command HighWaterMarkUpdated(long sequence)
    {
        return new Command { HighWaterMark = sequence, Type = CommandType.HighWater };
    }

    public static Command Started(long highWater, long lastCommitted)
    {
        return new Command { HighWaterMark = highWater, LastCommitted = lastCommitted };
    }
}
