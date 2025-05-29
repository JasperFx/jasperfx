using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class ProgressionProgressOutOfOrderException: Exception
{
    public ProgressionProgressOutOfOrderException(ShardName progressionOrShardName): base(
        $"Progression '{progressionOrShardName}' is out of order. This may happen when multiple processes try to process the projection")
    {
    }
}
