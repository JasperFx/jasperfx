namespace JasperFx.MultiTenancy;

/// <summary>
/// Marker interface that opts a document or entity type into conjoined
/// multi-tenancy within Critter Stack tools (Marten, Polecat, Wolverine).
/// The framework is responsible for setting <see cref="IHasTenantId.TenantId"/>
/// when the entity is written and for populating it from storage when the
/// entity is loaded, so application code should treat the value as framework-managed.
/// </summary>
public interface ITenanted : IHasTenantId
{
}
