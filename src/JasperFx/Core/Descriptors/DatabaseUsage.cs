namespace JasperFx.Core.Descriptors;

public class DatabaseUsage : OptionsDescription
{
    public DatabaseCardinality Cardinality { get; set; } = DatabaseCardinality.Single;
    
    public DatabaseDescriptor? MainDatabase { get; set; }
    
    // Also holds tenants
    public List<DatabaseDescriptor> Databases { get; set; } = [];
}