namespace JasperFx.Descriptors;

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
