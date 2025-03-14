namespace JasperFx.Events.Projections;

public enum TenancyBehavior
{
    /// <summary>
    /// This projection will process events for all tenants at one time,
    /// with the user being responsible for tenanted operations
    /// </summary>
    AcrossTenants,
    
    /// <summary>
    /// This projection will process events for one tenant at a time
    /// </summary>
    ByTenant
}