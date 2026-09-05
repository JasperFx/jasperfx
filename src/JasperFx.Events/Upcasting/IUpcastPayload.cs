using System.Text.Json;

namespace JasperFx.Events.Upcasting;

/// <summary>
/// The store-side seam of the shared upcasting contract: one stored event payload, about to be
/// deserialized, presented to an upcast transformation without exposing how the store actually
/// reads it.
/// </summary>
/// <remarks>
/// <para>
/// Originated in Marten, where the equivalent surface was the
/// <c>(ISerializer, DbDataReader, index)</c> triple threaded through
/// <c>Marten.Services.Json.Transformations.JsonTransformation</c>. That triple is PostgreSQL- and
/// Marten-serializer-shaped, so the promoted contract abstracts it to "a payload that can hand you
/// either a deserialized old CLR type or the raw JSON": each store implements this once as a thin
/// adapter over its own reader and serializer, at the same point in its hydration path where the
/// event-type name has been resolved and the payload is about to become an object.
/// </para>
/// <para>
/// An implementation is expected to be a short-lived, per-row adapter. Nothing here is cached or
/// reused; a transformation calls exactly one accessor exactly once per event.
/// </para>
/// </remarks>
public interface IUpcastPayload
{
    /// <summary>
    /// Deserialize the stored payload as <typeparamref name="T"/> through the store's configured
    /// serializer. This is what a typed <c>TOld → TNew</c> upcast uses to obtain the old CLR type.
    /// </summary>
    T As<T>() where T : notnull;

    /// <summary>
    /// Async counterpart of <see cref="As{T}"/>, for stores whose async read path can stream the
    /// payload rather than buffer it.
    /// </summary>
    ValueTask<T> AsAsync<T>(CancellationToken token) where T : notnull;

    /// <summary>
    /// The stored payload as a raw <see cref="JsonDocument"/>, for transformations that upcast
    /// without keeping the old CLR type in the codebase.
    /// </summary>
    /// <remarks>
    /// Available whenever the store persists events as System.Text.Json — always true for Polecat
    /// and Fisher, true for Marten when its serializer is STJ-based. A store whose configured
    /// serializer cannot produce a <see cref="JsonDocument"/> must throw
    /// <see cref="UpcastingException"/> here rather than guessing at an encoding. The caller owns
    /// disposing the returned document.
    /// </remarks>
    JsonDocument AsJsonDocument();

    /// <summary>
    /// Async counterpart of <see cref="AsJsonDocument"/>.
    /// </summary>
    ValueTask<JsonDocument> AsJsonDocumentAsync(CancellationToken token);
}
