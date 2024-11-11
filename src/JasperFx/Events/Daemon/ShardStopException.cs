namespace JasperFx.Events.Daemon;

/// <summary>
///     A projection shard failed to stop in a timely manner
/// </summary>
public class ShardStopException: Exception
{
    public ShardStopException(string projectionIdentity, Exception innerException): base(
        $"Failure while trying to stop '{projectionIdentity}'", innerException)
    {
    }
}


