namespace JasperFx.Core.Descriptions;

public interface IDatabaseUser  
{
    DatabaseCardinality Cardinality { get; }
    
    /// <summary>
    /// Evaluate the databases used at runtime
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token);
}