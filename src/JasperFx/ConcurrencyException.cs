using System.Runtime.Serialization;
using JasperFx.Core.Reflection;

namespace JasperFx;

public class ConcurrencyException: Exception
{
    public static string ToMessage(Type docType, object id)
    {
        var message = $"Optimistic concurrency check failed for {docType.FullName} #{id}";
        if (docType.CanBeCastTo<IRevisioned>())
        {
            message +=
                $". For documents of type {typeof(IRevisioned).FullNameInCode()}, JasperFx uses the current value of {nameof(IRevisioned)}.{nameof(IRevisioned.Version)} as the revision when a document storage operation is requested. You may need to explicitly call IDocumentSession.UpdateRevision() instead, or set the expected version correctly on the document itself";
        }

        return message;
    }

    
    public ConcurrencyException(Type docType, object id): base(ToMessage(docType, id))
    {
        DocType = docType.FullName!;
        Id = id;
    }

    public ConcurrencyException(string message, Type? docType, object id): base(message)
    {
        DocType = docType?.FullName!;
        Id = id;
    }

    public ConcurrencyException(string? message) : base(message)
    {
    }
    

    public string DocType { get; set; }
    public object Id { get; set; }
}
