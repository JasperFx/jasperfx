using System.Text.Json.Serialization;

namespace JasperFx.Core.Descriptions;

/// <summary>
/// Metadata about the usage of a database, including tenant information if any
/// </summary>
public class DatabaseDescriptor : OptionsDescription
{
    [JsonConstructor]
    public DatabaseDescriptor()
    {
    }

    public DatabaseDescriptor(object subject) : base(subject)
    {
    }

    /// <summary>
    /// Describes the basic type of database. Example: "PostgreSQL", "SqlServer", "RavenDb"
    /// </summary>
    public string Engine { get; init; } = string.Empty;

    /// <summary>
    /// Server name or location of the database if known
    /// </summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>
    /// Name of the database on the server for database engines that support this concept
    /// </summary>
    public string DatabaseName { get; init; } = string.Empty;
    
    /// <summary>
    /// If applicable, the main database schema or namespace for this usage
    /// </summary>
    public string SchemaOrNamespace { get; set; } = string.Empty;

    /// <summary>
    /// Just an application identifier within the system that does not necessarily reflect the database name. Commonly used for multi-tenancy
    /// usages
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// What tenant ids are stored in this database in the case of multi-tenancy
    /// </summary>
    public List<string> TenantIds { get; set; } = new();
    
    

    protected bool Equals(DatabaseDescriptor other)
    {
        return Engine == other.Engine && ServerName == other.ServerName && DatabaseName == other.DatabaseName && SchemaOrNamespace == other.SchemaOrNamespace && Identifier == other.Identifier;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
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

        return Equals((DatabaseDescriptor)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Engine, ServerName, DatabaseName, SchemaOrNamespace, Identifier);
    }
}