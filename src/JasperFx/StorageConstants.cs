namespace JasperFx;

public static class StorageConstants
{
    public const string DefaultTenantId = "*DEFAULT*";
    
    public static readonly string TombstoneStreamKey = "mt_tombstone";
    public static readonly Guid TombstoneStreamId = Guid.NewGuid();
}