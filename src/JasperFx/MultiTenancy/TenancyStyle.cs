namespace JasperFx.MultiTenancy;

/// <summary>
/// Describes how a document store partitions data across tenants. Lifted from the
/// byte-identical enum that lived in Marten's <c>Marten.Storage.TenancyStyle</c> and
/// inline in Polecat's <c>StoreOptions</c>. Canonical home is JasperFx.MultiTenancy,
/// beside <see cref="TenantIdStyle"/> and <see cref="IHasTenantId"/>.
/// </summary>
/// <remarks>
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>). Each store
/// type-forwards its old public name to this type so downstream apps importing
/// <c>Marten.Storage.TenancyStyle</c> keep compiling.
/// </remarks>
public enum TenancyStyle
{
    /// <summary>
    ///     No multi-tenancy, the default mode
    /// </summary>
    Single,

    /// <summary>
    ///     Multi-tenanted within the same database/schema through a tenant id
    /// </summary>
    Conjoined
}
