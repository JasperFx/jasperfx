namespace JasperFx.Events.Daemon;

/// <summary>
///     A projection shard failed to start
/// </summary>
public class ShardStartException: Exception
{
    internal ShardStartException(string projectionIdentity, Exception innerException): base(
        $"Failure while trying to start '{projectionIdentity}'", innerException)
    {
    }

    internal ShardStartException(string projectionIdentity, string reason): base(
        $"Unable to start a subscription agent for '{projectionIdentity}'. {reason}")
    {
    }
}
