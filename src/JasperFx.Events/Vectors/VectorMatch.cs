namespace JasperFx.Events.Vectors;

/// <summary>
/// One result of a vector search: the document and how far it was from the query vector.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Document">The matched document, materialized exactly as a query would return it.</param>
/// <param name="Distance">
/// The distance under the <see cref="DistanceFunction" /> the search ran with. Smaller is closer on
/// every store and every metric; for <see cref="DistanceFunction.Cosine" /> it is
/// <c>1 − similarity</c>, so <c>0</c> is identical and <c>1</c> is orthogonal.
/// </param>
/// <remarks>
/// <para>
/// Exists because a document alone is not enough for two callers the stores have to serve: anything
/// applying a similarity floor ("nothing below 0.7") and anything fusing this ranking with a full
/// text ranking. Marten.PgVector's document-level search returned bare documents and only its
/// projection-table search surfaced a distance; the scored overload on every store returns this
/// type instead. See <see href="https://github.com/JasperFx/jasperfx/issues/811" />.
/// </para>
/// <para>
/// A <see cref="double" /> even though every store computes in single precision, so a caller can
/// combine distances arithmetically without a cast at each site. Not the raw <c>float</c> the
/// database returned: two stores hand it back as <c>float</c>, one as <c>double</c>, and one
/// declared type beats a per-store difference a consumer would have to know.
/// </para>
/// </remarks>
public sealed record VectorMatch<T>(T Document, double Distance);
