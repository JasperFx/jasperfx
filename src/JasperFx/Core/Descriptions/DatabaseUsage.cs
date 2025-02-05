namespace JasperFx.Core.Descriptions;

public enum DatabaseUsage
{
    /// <summary>
    /// No database usage here of any sort
    /// </summary>
    None,
    
    /// <summary>
    /// Using a single database regardless of tenancy
    /// </summary>
    Single,
    
    /// <summary>
    /// Using a static number of databases
    /// </summary>
    StaticMultiple,
    
    /// <summary>
    /// Using a dynamic number of databases that should
    /// be expected to potentially change at runtime
    /// </summary>
    DynamicMultiple
}

public class DatabaseCapability : OptionsDescription
{
    public DatabaseUsage Usage { get; set; } = DatabaseUsage.Single;
    
    // Also holds tenants
    public List<DatabaseDescription> Databases { get; set; } = [];
}

public class EventStoreUsage
{
    public OptionsDescription Configuration { get; set; }
    //public DatabaseCapability Database { get; set; }
}


public class ProjectionDescription : OptionsDescription
{
    // Information about filters
    // Information about events used
    // Information about stream types
}

public record EventDescriptor(string EventTypeName, TypeDescriptor Type);

public class EventStoreCapability : DatabaseCapability
{
    public List<EventDescriptor> Events { get; set; } = [];
    public List<TypeDescriptor> StreamTypes { get; set; } = [];
    public List<ProjectionDescription> Projections { get; set; } = [];
}

public class DatabaseDescription : OptionsDescription
{
    /// <summary>
    /// Identifier within the Wolverine system
    /// </summary>
    public string DatabaseIdentifier { get; set; } = "Default";

    /// <summary>
    /// Descriptive name for the database engine. Example: "SqlServer" or "PostgreSQL"
    /// </summary>
    public string DatabaseEngine { get; set; } = "Unknown";

    public List<string> TenantIds { get; set; } = new();
}

// This definitely goes into JasperFx.Core. Also need a way to 
// get out tenanted message stores too though. Put something separate
// in Weasel for multi-tenancy for EF Core that can generate databases
public interface IDatabaseUser  
{
    DatabaseUsage Usage { get; }
    
    /// <summary>
    /// Evaluate the databases used at runtime
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    ValueTask<IReadOnlyList<DatabaseDescription>> DescribeDatabasesAsync(CancellationToken token);
}