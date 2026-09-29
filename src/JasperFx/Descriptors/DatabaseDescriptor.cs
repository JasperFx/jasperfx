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

        var parts = new List<string>
        {
            serverName,
            DatabaseName,
            SchemaOrNamespace
        }.Where(x => x.IsNotEmpty()).ToArray();

        // Whichever part lands first is the host, and the host obeys different rules than the path
        // segments behind it. Keeping the "drop the empties, then take the first" order means a
        // descriptor with no ServerName still names itself after its database, as it always has.
        var tail = parts.Skip(1).Select(Uri.EscapeDataString).Join("/");
        var scheme = Engine.ToLowerInvariant();
        var host = parts.Length == 0 ? string.Empty : sanitizeHost(parts[0]);

        var text = tail.IsEmpty() ? $"{scheme}://{host}" : $"{scheme}://{host}/{tail}";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        // A host of permitted characters can still be refused for its SHAPE: an empty DNS label, as
        // in ".\SQLEXPRESS" sanitizing to "._sqlexpress", or an interior "..". Folding the dots in
        // resolves every such case. Deliberately a fallback rather than an unconditional rule,
        // because "." on its own IS a legal host and IS a real SQL Server data source (the local
        // default instance) -- collapsing dots up front would rename it for no reason. Reaching here
        // at all means the URI could not be built, so nothing that works today takes this path.
        host = host.Replace('.', '_');

        return new Uri(tail.IsEmpty() ? $"{scheme}://{host}" : $"{scheme}://{host}/{tail}");
    }

    /// <summary>
    /// Map a server name onto the character set a URI host actually permits, replacing everything
    /// else with an underscore.
    /// </summary>
    /// <remarks>
    /// A server name cannot simply be escaped into the host position: <c>Uri.EscapeDataString</c>
    /// percent-encodes anything outside the RFC 3986 unreserved set, and a percent-escape is not
    /// legal in a host, so <c>new Uri</c> throws "The hostname could not be parsed". That took out
    /// every SQL Server named instance (<c>db-host\MSSQL2017</c>), SQL Express (<c>.\SQLEXPRESS</c>)
    /// and LocalDB (<c>(localdb)\MSSQLLocalDB</c>) — 27 printable ASCII characters in all.
    ///
    /// Sanitizing against the permitted set replaces enumerating the offenders one report at a time,
    /// which is how this method acquired a comma split and then a <c>/</c> and <c>:</c> replacement.
    /// Note <c>~</c> is unreserved for escaping purposes yet still rejected in a host, so the two
    /// sets are genuinely different and this one is the one that matters here.
    ///
    /// This preserves every identity that works today. A server name reaches the host unchanged only
    /// if every character is already in this set — anything else percent-encoded and threw — so the
    /// inputs whose URI changes are exactly the inputs that used to crash. That matters because
    /// <see cref="DatabaseUri"/> is load-bearing as an identity elsewhere (agent URIs, database ids).
    /// </remarks>
    private static string sanitizeHost(string serverName)
    {
        return new string(serverName
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_')
            .ToArray());
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