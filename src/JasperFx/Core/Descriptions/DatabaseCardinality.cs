using JasperFx.Core.Reflection;

namespace JasperFx.Core.Descriptions;

public enum DatabaseCardinality
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

public class DatabaseUsage : OptionsDescription
{
    public DatabaseCardinality Cardinality { get; set; } = DatabaseCardinality.Single;
    
    public DatabaseDescriptor? MainDatabase { get; set; }
    
    // Also holds tenants
    public List<DatabaseDescriptor> Databases { get; set; } = [];
}


public enum SubscriptionType
{
    SingleStreamProjection,
    MultiStreamProjection,
    Subscription,
    FlatTableProjection,
    EventProjection
    
}

public class SubscriptionDescriptor : OptionsDescription
{
    public SubscriptionType SubscriptionType { get; }

    public SubscriptionDescriptor(SubscriptionType subscriptionType)
    {
        SubscriptionType = subscriptionType;
    }

    public SubscriptionDescriptor(object subject, SubscriptionType subscriptionType) : base(subject)
    {
        SubscriptionType = subscriptionType;
    }

    public List<EventDescriptor> Events { get; set; } = new();
}

public record EventDescriptor(string EventTypeName, TypeDescriptor Type);

public class EventStoreUsage : OptionsDescription
{
    public EventStoreUsage()
    {
    }

    public EventStoreUsage(Type storeType, object options) : base(options)
    {
        StoreIdentifier = storeType.FullNameInCode();
    }

    public string StoreIdentifier { get; set; }
    public DatabaseUsage Database { get; set; }
    public List<EventDescriptor> Events { get; set; } = new();
    public List<SubscriptionDescriptor> Subscriptions { get; set; } = new();
}

/// <summary>
/// Service to create a description of the EventStoreUsage in the application
/// </summary>
public interface IEventStoreCapability
{
    Task<EventStoreUsage?> TryCreateUsage(CancellationToken token);
}

/// <summary>
/// Metadata about the usage of a database, including tenant information if any
/// </summary>
public class DatabaseDescriptor : OptionsDescription
{
    public DatabaseDescriptor()
    {
    }

    public DatabaseDescriptor(object subject) : base(subject)
    {
    }
    
    public string Engine { get; init; }
    public string ServerName { get; init; }
    public string DatabaseName { get; init; }
    public string SchemaOrNamespace { get; set; } = string.Empty;

    /// <summary>
    /// What tenant ids are stored in this database in the case of multi-tenancy
    /// </summary>
    public List<string> TenantIds { get; set; } = new();
}

// This definitely goes into JasperFx.Core. Also need a way to 
// get out tenanted message stores too though. Put something separate
// in Weasel for multi-tenancy for EF Core that can generate databases
public interface IDatabaseUser  
{
    DatabaseCardinality Cardinality { get; }
    
    /// <summary>
    /// Evaluate the databases used at runtime
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    ValueTask<IReadOnlyList<DatabaseDescriptor>> DescribeDatabasesAsync(CancellationToken token);
}