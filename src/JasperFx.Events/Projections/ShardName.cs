using System.Text.Json.Serialization;

namespace JasperFx.Events.Projections;

/// <summary>
///     Identity for a single async shard
/// </summary>
public class ShardName
{
    public const string All = "All";

    /// <summary>
    ///     When this shard is a member of a composite projection, the 1-based ordinal of the
    ///     <c>ProjectionStage</c> it runs in (stages run sequentially; members within a stage run
    ///     in parallel). Null for non-composite shards. Set via <see cref="ForCompositeStage" />
    ///     when the composite machinery composes each member's shard — metadata only, it does NOT
    ///     participate in <see cref="Identity" /> so progression-row keys are unchanged.
    /// </summary>
    public uint? StageNumber { get; private set; }

    /// <summary>
    ///     When this shard is a member of a composite projection, the name of the parent composite.
    ///     Null for non-composite shards. See <see cref="StageNumber" />.
    /// </summary>
    public string? CompositeParentName { get; private set; }

    /// <summary>
    ///     Attach composite-membership metadata (parent composite name + stage ordinal) to this
    ///     shard. Additive + fluent so it doesn't disturb <see cref="Compose" />/the constructors
    ///     or <see cref="Identity" />. Returns the same instance for call-site chaining.
    /// </summary>
    public ShardName ForCompositeStage(uint stageNumber, string compositeParentName)
    {
        StageNumber = stageNumber;
        CompositeParentName = compositeParentName;
        return this;
    }


    [JsonConstructor]
    public ShardName(string name, string shardKey, uint version, string? tenantId)
    {
        Name = name;
        ShardKey = shardKey;
        Version = version;
        TenantId = string.IsNullOrEmpty(tenantId) ? null : tenantId;

        // The tenant is a distinct, trailing slot in the grammar -- it is NEVER folded
        // into the ShardKey. A null/empty tenant means "store-global" and keeps the
        // Identity/RelativeUrl byte-for-byte identical to the pre-tenancy behavior.
        var identitySuffix = TenantId == null ? string.Empty : $":{TenantId}";
        var urlSuffix = TenantId == null ? string.Empty : $"/{TenantId}";

        if (version > 1)
        {
            Identity = $"{name}:V{version}:{shardKey}{identitySuffix}";
            RelativeUrl = $"{name}/{shardKey}/v{version}{urlSuffix}".ToLowerInvariant();
        }
        else
        {
            Identity = $"{name}:{shardKey}{identitySuffix}";
            RelativeUrl = $"{name}/{shardKey}{urlSuffix}".ToLowerInvariant();
        }

        // The high-water mark is addressed by a bare, well-known identity rather than the
        // Name:ShardKey grammar -- but ONLY when it is store-global. jasperfx#618: a per-tenant
        // high-water row is a real, persisted row shape (Marten writes HighWaterMark:{tenant} on
        // every vectorized per-tenant poll), and flattening those to the store-global constant made
        // every tenant's mark claim to be the store's, silently.
        if (name == ShardState.HighWaterMark)
        {
            Identity = TenantId == null ? ShardState.HighWaterMark : $"{ShardState.HighWaterMark}:{TenantId}";
        }

    }

    public ShardName(string name, string shardKey, uint version): this(name, shardKey, version, null)
    {
    }

    public ShardName(string name): this(name, All, 1, null)
    {
    }

    /// <summary>
    ///     Compose a <see cref="ShardName" /> from its parts. The optional <paramref name="tenantId" />
    ///     occupies a distinct, trailing slot in the shard grammar and is never folded into the
    ///     <paramref name="shardKey" />. A null/empty tenant is store-global (today's default behavior).
    /// </summary>
    /// <param name="name">Projection or composite-group identity</param>
    /// <param name="shardKey">Identity of the shard within the projection. Defaults to "All".</param>
    /// <param name="tenantId">Optional tenant partition suffix. Null/empty means store-global.</param>
    /// <param name="version">Projection version. Defaults to 1.</param>
    public static ShardName Compose(string name, string? shardKey = All, string? tenantId = null, uint version = 1)
    {
        return new ShardName(name, string.IsNullOrEmpty(shardKey) ? All : shardKey, version, tenantId);
    }

    /// <summary>
    ///     Compose the high-water progression identity for a tenant partition — <c>HighWaterMark</c>
    ///     store-global, <c>HighWaterMark:{tenant}</c> for a tenant. This is the supported way to
    ///     address the per-tenant high-water rows an event store writes under per-tenant event
    ///     partitioning, and it round-trips through <see cref="TryParse" />. See jasperfx#618.
    /// </summary>
    /// <param name="tenantId">Tenant partition. Null/empty is the store-global mark.</param>
    public static ShardName HighWaterMarkFor(string? tenantId = null)
    {
        return new ShardName(ShardState.HighWaterMark, All, 1, tenantId);
    }

    /// <summary>
    ///     True when this name addresses a high-water progression row rather than a projection or
    ///     subscription shard — either the store-global mark or one tenant's mark
    ///     (<see cref="TenantId" /> tells them apart). High-water rows are bookkeeping: they never
    ///     belong to a registered projection and never advance like a shard, so a consumer walking
    ///     the progression table must exclude them. See jasperfx#618.
    /// </summary>
    public bool IsHighWaterMark => Name == ShardState.HighWaterMark;

    /// <summary>
    ///     Parse a shard <see cref="Identity" /> string back into a <see cref="ShardName" />.
    ///     Understands every form produced by <see cref="Compose" />/<see cref="Identity" />:
    ///     <c>Name:ShardKey</c>, <c>Name:ShardKey:Tenant</c>, <c>Name:V{n}:ShardKey</c>, and
    ///     <c>Name:V{n}:ShardKey:Tenant</c>. A leading <c>V{digits}</c> segment is interpreted as a
    ///     version marker; otherwise the trailing segment of a 3-part identity is the tenant.
    ///     <para>
    ///     The high-water progression rows have their own grammar — <c>HighWaterMark</c> and
    ///     <c>HighWaterMark:{tenant}</c> (see <see cref="HighWaterMarkFor" />) — and are the only
    ///     names that do not carry a shard key. Any other <c>HighWaterMark</c>-prefixed string is
    ///     rejected rather than forced into the generic grammar, because the result would not
    ///     round-trip its own <see cref="Identity" />.
    ///     </para>
    /// </summary>
    public static bool TryParse(string? text, out ShardName? shardName)
    {
        shardName = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text == ShardState.HighWaterMark)
        {
            shardName = new ShardName(ShardState.HighWaterMark);
            return true;
        }

        var parts = text.Split(':');

        // jasperfx#618: HighWaterMark:{tenant} is a real persisted row shape. Parsing it through the
        // generic Name:ShardKey branch put the tenant id in the ShardKey slot, left TenantId null,
        // and collapsed Identity back to the bare store-global constant -- true, plus a wrong answer.
        if (parts[0] == ShardState.HighWaterMark)
        {
            if (parts.Length != 2 || string.IsNullOrEmpty(parts[1]))
            {
                return false;
            }

            shardName = HighWaterMarkFor(parts[1]);
            return true;
        }

        switch (parts.Length)
        {
            case 2:
                // Name:ShardKey
                shardName = new ShardName(parts[0], parts[1], 1, null);
                return true;

            case 3 when TryParseVersion(parts[1], out var versionedKeyVersion):
                // Name:V{n}:ShardKey
                shardName = new ShardName(parts[0], parts[2], versionedKeyVersion, null);
                return true;

            case 3:
                // Name:ShardKey:Tenant
                shardName = new ShardName(parts[0], parts[1], 1, parts[2]);
                return true;

            case 4 when TryParseVersion(parts[1], out var versionedTenantVersion):
                // Name:V{n}:ShardKey:Tenant
                shardName = new ShardName(parts[0], parts[2], versionedTenantVersion, parts[3]);
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseVersion(string segment, out uint version)
    {
        version = 1;
        if (segment.Length < 2 || segment[0] != 'V')
        {
            return false;
        }

        return uint.TryParse(segment.AsSpan(1), out version);
    }

    public string RelativeUrl { get; }

    public ShardName CloneForDatabase(Uri database)
    {
        return new ShardName(Name, ShardKey, Version, TenantId) { Database = database };
    }

    /// <summary>
    ///     Return an equivalent <see cref="ShardName" /> bound to the supplied tenant. A null/empty
    ///     tenant yields the store-global shard.
    /// </summary>
    public ShardName ForTenant(string? tenantId)
    {
        return new ShardName(Name, ShardKey, Version, tenantId) { Database = Database };
    }

    public Uri Database { get; set; } = new Uri("database://default");

    /// <summary>
    ///     Parent projection name
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     The identity of the shard within the projection. If there is only
    ///     one shard for a projection, this will be "All"
    /// </summary>
    public string ShardKey { get; }

    /// <summary>
    ///     Optional tenant partition this shard is scoped to. Null means store-global -- the only
    ///     behavior that existed before per-tenant partitioning. This is a distinct slot in the shard
    ///     grammar and is never folded into <see cref="ShardKey" />.
    /// </summary>
    public string? TenantId { get; }

    /// <summary>
    ///     {ProjectionName}:{Key}. Single identity string that should be unique within this application
    /// </summary>
    public string Identity { get; }

    public uint Version { get; } = 1;

    public override string ToString()
    {
        return $"{nameof(Identity)}: {Identity}";
    }


    protected bool Equals(ShardName other)
    {
        return Identity == other.Identity;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((ShardName)obj);
    }

    public override int GetHashCode()
    {
        return Identity != null ? Identity.GetHashCode() : 0;
    }
}
