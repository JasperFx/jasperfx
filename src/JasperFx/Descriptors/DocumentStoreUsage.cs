using JasperFx.Core.Reflection;

namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic snapshot of a Critter Stack document store (Marten's
/// <c>IDocumentStore</c>, Polecat's equivalent). Surfaces the operationally-
/// interesting configuration shape — schema identity, tenancy, document-type
/// mappings, and code-generation policy — for monitoring tools such as
/// CritterWatch.
/// </summary>
/// <remarks>
/// <para>
/// This is the parallel of <c>JasperFx.Events.Descriptors.EventStoreUsage</c>:
/// a hand-built descriptor with first-class typed properties, deliberately
/// side-stepping the magical <see cref="OptionsDescription.Children"/> /
/// <see cref="OptionsDescription.Sets"/> traversal so the wire shape stays
/// stable across Marten / Polecat versions and serializes cleanly.
/// </para>
/// <para>
/// Properties living on the base <see cref="OptionsDescription.Properties"/>
/// bag (the flat OptionValues) carry settings that are interesting but don't
/// warrant their own first-class slot: tenancy quirks, command timeout, batch
/// size, hilo defaults, multi-host preferences, etc. They're populated by
/// the implementing store inside <c>TryCreateUsage</c>.
/// </para>
/// </remarks>
public class DocumentStoreUsage : OptionsDescription
{
    public DocumentStoreUsage()
    {
    }

    /// <summary>
    /// Build a usage descriptor for <paramref name="subject"/> identified by
    /// <paramref name="subjectUri"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT chain into <see cref="OptionsDescription(object)"/>:
    /// that ctor invokes the reflective property auto-reader, which on a
    /// concrete <c>DocumentStore</c> subject walks <c>Storage</c> /
    /// <c>Advanced</c> / <c>Diagnostics</c> / <c>Options</c> handles and dumps
    /// them into <see cref="OptionsDescription.Children"/> /
    /// <see cref="OptionsDescription.Properties"/>. Those are runtime handles,
    /// not configuration shape; surfacing them in CritterWatch's Documents tab
    /// is noise that operators have to scroll past on every visit.
    ///
    /// Callers (Marten's <c>DocumentStore.TryCreateUsage</c>, Polecat's
    /// equivalent) populate the first-class fields explicitly —
    /// <see cref="Database"/>, <see cref="Documents"/>,
    /// <see cref="CodeGeneration"/>, <see cref="StoreName"/>,
    /// <see cref="DatabaseSchemaName"/>, etc. — and any extra
    /// <see cref="OptionsDescription.AddValue"/> entries explicitly. Nothing
    /// here implicitly walks the subject.
    /// </remarks>
    public DocumentStoreUsage(Uri subjectUri, object subject)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        Subject = subject.GetType().FullNameInCode();
        SubjectUri = subjectUri;
        Version = subject.GetType().Assembly.GetName().Version?.ToString();
    }

    /// <summary>
    /// Version of the assembly that produced this snapshot — typically the
    /// version of <c>Marten.dll</c> or <c>Polecat.dll</c>.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Stable URI identifying this document store within the host process.
    /// Mirrors <see cref="EventStoreUsage.SubjectUri"/>; e.g. <c>marten://main</c>
    /// or <c>polecat://main</c>.
    /// </summary>
    public Uri SubjectUri { get; set; } = null!;

    /// <summary>
    /// Database topology — server / database / cardinality. Same shape used by
    /// <see cref="EventStoreUsage.Database"/>, populated from
    /// <c>Tenancy.DescribeDatabasesAsync</c>.
    /// </summary>
    public DatabaseUsage Database { get; set; } = null!;

    /// <summary>
    /// Logical store name disambiguating multiple <c>IDocumentStore</c>s in DI
    /// — defaults to <c>"Main"</c>, ancillary stores carry their registered
    /// store name.
    /// </summary>
    public string StoreName { get; set; } = "";

    /// <summary>
    /// Default Postgres / SQL Server schema name where document tables live
    /// — operator-visible because it's the prefix on every table this store
    /// generates.
    /// </summary>
    public string DatabaseSchemaName { get; set; } = "";

    /// <summary>
    /// Headline schema-mutation gate — one of <c>None</c>,
    /// <c>CreateOnly</c>, <c>CreateOrUpdate</c>, <c>All</c>. First-class
    /// because operators consistently want to see at a glance whether the
    /// store will mutate the database on its own.
    /// </summary>
    public string AutoCreateSchemaObjects { get; set; } = "";

    /// <summary>
    /// Store-wide enum-storage default — <c>"AsInteger"</c> or
    /// <c>"AsString"</c>. First-class because it's a write-once decision with
    /// data-compatibility consequences; operators staring at a config dump
    /// want it called out, not buried in the flat OptionValues bag.
    /// </summary>
    public string EnumStorage { get; set; } = "";

    /// <summary>
    /// Code-generation snapshot — application assembly, output path, mode,
    /// source-writing toggle. Wrapped as a child descriptor (rather than
    /// flattened) so the relationship between the four obsolete-on-StoreOptions
    /// properties stays cohesive even after they ride out the deprecation
    /// window.
    /// </summary>
    public CodeGenerationDescriptor? CodeGeneration { get; set; }

    /// <summary>
    /// Per-document-type mappings — the real meat of "what does this
    /// DocumentStore manage". Each entry mirrors a <c>DocumentMapping</c>
    /// (configuration shape + the DDL the store will apply). Sourced from
    /// <c>Storage.AllDocumentMappings</c>.
    /// </summary>
    public List<DocumentMappingDescriptor> Documents { get; set; } = new();
}
