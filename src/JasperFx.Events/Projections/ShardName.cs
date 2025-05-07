using System.Text.Json.Serialization;

namespace JasperFx.Events.Projections;

/// <summary>
///     Identity for a single async shard
/// </summary>
public class ShardName
{
    public const string All = "All";

    
    [JsonConstructor]
    public ShardName(string name, string shardKey, uint version)
    {
        Name = name;
        ShardKey = shardKey;
        Version = version;

        if (version > 1)
        {
            Identity = $"{name}:V{version}:{shardKey}";
        }
        else
        {
            Identity = $"{name}:{shardKey}";
        }
        
        if (name == ShardState.HighWaterMark)
        {
            Identity = ShardState.HighWaterMark;
        }

    }

    public ShardName(string name): this(name, All, 1)
    {
    }

    public ShardName CloneForDatabase(Uri database)
    {
        return new ShardName(Name, ShardKey, Version) { Database = database };
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
