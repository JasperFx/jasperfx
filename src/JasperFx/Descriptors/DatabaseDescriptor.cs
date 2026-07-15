using System.Text.Json.Serialization;
using JasperFx.Core;

namespace JasperFx.Descriptors;

/// <summary>
/// Metadata about the usage of a database, including tenant information if any
/// </summary>
public class DatabaseDescriptor : OptionsDescription
{
    [JsonConstructor]
    public DatabaseDescriptor()
    {
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Inherits the OptionsDescription ctor's reflective property read of subject's runtime type.")]
    public DatabaseDescriptor(object subject) : base(subject)
    {
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Inherits the OptionsDescription ctor's reflective property read of subject's runtime type.")]
    public DatabaseDescriptor(object subject, Uri subjectUri) : base(subject)
    {
        SubjectUri = subjectUri;
    }

    public Uri SubjectUri { get; set; } = "database://unknown".ToUri();

    /// <summary>
    /// Describes the basic type of database. Example: "PostgreSQL", "SqlServer", "RavenDb"
    /// </summary>
    public string Engine { get; init; } = string.Empty;

    /// <summary>
    /// Server name or location of the database if known
    /// </summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>
    /// The port the server listens on, for engines that carry it separately from
    /// <see cref="ServerName"/> — PostgreSQL does; SQL Server folds it into the Data Source
    /// (<c>host,1433</c>) and should leave this null.
    /// </summary>
    /// <remarks>
    /// Null when unknown, which is what every descriptor built before this property existed will
    /// report. Consumers that key on the server — a per-server connection budget, say — need it:
    /// <see cref="ServerName"/> is the host alone, so two clusters co-hosted on one box are
    /// otherwise indistinguishable. Deliberately NOT part of <see cref="DatabaseUri"/>: that URI is
    /// already load-bearing as an identity elsewhere, and folding a new segment into it would
    /// silently rename existing databases.
    /// </remarks>
    public int? Port { get; init; }

    /// <summary>
    /// Name of the database on the server for database engines that support this concept
    /// </summary>
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>
    /// If applicable, the main database schema or namespace for this usage
    /// </summary>
    public string SchemaOrNamespace { get; set; } = string.Empty;

    public Uri DatabaseUri()
    {
        var serverName = ServerName.Contains(',') ? ServerName.Split(',')[0] : ServerName;

        // Sanitize the server name for use as a URI hostname. Unix socket paths
        // (e.g. /cloudsql/platform-dev:europe-west4:shared-db) contain characters
        // that are invalid in URI hostnames.
        serverName = serverName.Replace('/', '_').Replace(':', '_');

        var parts = new List<string>
        {
            serverName,
            DatabaseName,
            SchemaOrNamespace
        };

        return new Uri($"{Engine.ToLowerInvariant()}://{parts.Where(x => x.IsNotEmpty()).Select(Uri.EscapeDataString).Join("/")}");
    }

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
        return Engine == other.Engine && ServerName == other.ServerName && Port == other.Port && DatabaseName == other.DatabaseName && SchemaOrNamespace == other.SchemaOrNamespace && Identifier == other.Identifier;
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
        return HashCode.Combine(Engine, ServerName, Port, DatabaseName, SchemaOrNamespace, Identifier);
    }
}