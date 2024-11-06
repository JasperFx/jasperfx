namespace JasperFx.Events.Projections;

/// <summary>
///     A projection shard failed to start
/// </summary>
public class ShardStartException: Exception
{
    internal ShardStartException(string projectionIdentity, Exception innerException): base(
        $"Failure while trying to stop '{projectionIdentity}'", innerException)
    {
    }
}
