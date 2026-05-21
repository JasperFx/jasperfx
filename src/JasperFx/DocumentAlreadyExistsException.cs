namespace JasperFx;

/// <summary>
/// Thrown when an Insert is attempted for a document whose id already exists in storage.
/// </summary>
/// <remarks>
/// Lifted and reconciled from the conceptually-shared exception that lived in both Marten
/// (<c>Marten.Exceptions.DocumentAlreadyExistsException : MartenException</c>, carrying an inner
/// exception, a <c>DocType</c> (Type) and <c>Id</c>, plus a legacy serialization ctor) and Polecat
/// (<c>Polecat.Exceptions.DocumentAlreadyExistsException : Exception</c>, with <c>DocumentType</c>
/// (Type) and <c>Id</c>, no inner exception). Canonical home is JasperFx core next to
/// <see cref="ConcurrencyException"/>, on a plain <see cref="Exception"/> base. Part of the Critter
/// Stack 2026 dedupe pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>),
/// carved out of <see href="https://github.com/JasperFx/jasperfx/issues/328">#328</see> as
/// <see href="https://github.com/JasperFx/jasperfx/issues/338">#338</see>.
///
/// Reconciliation decisions (the three differing axes):
/// <list type="bullet">
/// <item><description>
/// <b>Property name:</b> canonical <see cref="DocumentType"/> (Polecat's name; descriptive and avoids
/// colliding with <see cref="ConcurrencyException"/>'s <c>DocType</c>, which is a <c>string</c> FullName,
/// not a Type). A get-only <see cref="DocType"/> alias is kept for Marten source-compat.
/// </description></item>
/// <item><description>
/// <b>Message format:</b> Marten's <c>"Document already exists {FullName}: {id}"</c> — uses FullName so
/// types sharing a simple name across namespaces stay unambiguous.
/// </description></item>
/// <item><description>
/// <b>Ctors:</b> both the inner-exception form (Marten, inner is nullable since Marten passes
/// <c>null</c> from its closed-shape insert path) and the no-inner form (Polecat). The legacy
/// <c>(SerializationInfo, StreamingContext)</c> ctor is dropped — binary exception serialization is
/// obsolete on .NET 8+ (SYSLIB0051) and JasperFx targets net9/net10.
/// </description></item>
/// </list>
/// </remarks>
public class DocumentAlreadyExistsException : Exception
{
    public DocumentAlreadyExistsException(Type documentType, object id)
        : base(ToMessage(documentType, id))
    {
        DocumentType = documentType;
        Id = id;
    }

    public DocumentAlreadyExistsException(Exception? inner, Type documentType, object id)
        : base(ToMessage(documentType, id), inner)
    {
        DocumentType = documentType;
        Id = id;
    }

    public static string ToMessage(Type documentType, object id)
        => $"Document already exists {documentType.FullName}: {id}";

    public Type DocumentType { get; }
    public object Id { get; }

    /// <summary>
    /// Alias for <see cref="DocumentType"/>, retained for source compatibility with Marten's
    /// original <c>DocType</c> property.
    /// </summary>
    public Type DocType => DocumentType;
}
