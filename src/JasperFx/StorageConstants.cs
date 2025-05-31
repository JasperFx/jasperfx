namespace JasperFx;

public static class StorageConstants
{
    public const string DefaultTenantId = "*DEFAULT*";
    
    public static readonly string TombstoneStreamKey = "mt_tombstone";
    public static readonly Guid TombstoneStreamId = Guid.NewGuid();
    public static readonly string TenantIdColumn = "tenant_id";
    public static readonly string ConnectionStringColumn = "connection_string";

    public static readonly string Main = "Main";
}