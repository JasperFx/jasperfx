namespace JasperFx.Descriptors;

/// <summary>
/// Generic interface for any service that uses a database and can describe itself
/// </summary>
public interface IDatabaseUser  
{
    /// <summary>
    /// Is this single, dynamic multi-, or static-multi tenanted?
    /// </summary>
    DatabaseCardinality Cardinality { get; }
    
    /// <summary>
    /// Evaluate the databases used at runtime
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token);
}