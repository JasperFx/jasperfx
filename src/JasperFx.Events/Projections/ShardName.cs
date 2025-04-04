namespace JasperFx.Events.Projections;

/// <summary>
///     Identity for a single async shard
/// </summary>
public class ShardName
{
    public const string All = "All";

    public ShardName(string projectionName, string key, uint version)
    {
        ProjectionName = projectionName;
        Key = key;
        Version = version;

        if (version > 1)
        {
            Identity = $"{projectionName}:V{version}:{key}";
        }
        else
        {
            Identity = $"{projectionName}:{key}";
        }
        
        if (projectionName == ShardState.HighWaterMark)
        {
            Identity = ShardState.HighWaterMark;
        }

    }

    public ShardName(string projectionName, string key) : this(projectionName, key, 1u)
    {

    }

    public ShardName(string projectionName): this(projectionName, All, 1)
    {
    }

    public ShardName CloneForDatabase(Uri database)
    {
        return new ShardName(ProjectionName, Key, Version) { Database = database };
    }

    public Uri Database { get; set; } = new Uri("database://default");

    /// <summary>
    ///     Parent projection name
    /// </summary>
    public string ProjectionName { get; }

    /// <summary>
    ///     The identity of the shard within the projection. If there is only
    ///     one shard for a projection, this will be "All"
    /// </summary>
    public string Key { get; }

    /// <summary>
    ///     {ProjectionName}:{Key}. Single identity string that should be unique within this Marten application
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

    public override bool Equals(object obj)
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
