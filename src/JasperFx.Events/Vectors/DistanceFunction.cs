namespace JasperFx.Events.Vectors;

/// <summary>
/// The metric a vector search ranks by. Every member is a <em>distance</em>: smaller means closer,
/// on every store, so a caller can order ascending without knowing which database answered.
/// </summary>
/// <remarks>
/// <para>
/// The member names are Marten.PgVector's, kept verbatim so Marten can alias its own enum onto this
/// one without renaming anything a user has written. The numeric values are not part of the
/// contract and are not stable across packages; never persist or compare them.
/// </para>
/// <para>
/// What each member becomes per store is the store's business, but it is pinned here because the
/// "smaller is closer" promise depends on it:
/// </para>
/// <list type="table">
/// <listheader><term>Member</term><description>Marten (pgvector) / Polecat (SQL Server 2025) / Fisher (SQLite)</description></listheader>
/// <item><term><see cref="Cosine" /></term><description><c>&lt;=&gt;</c> / <c>VECTOR_DISTANCE('cosine', …)</c> / a registered <c>cosine_distance()</c> function</description></item>
/// <item><term><see cref="L2" /></term><description><c>&lt;-&gt;</c> / <c>VECTOR_DISTANCE('euclidean', …)</c> / a registered function</description></item>
/// <item><term><see cref="InnerProduct" /></term><description><c>&lt;#&gt;</c> / <c>VECTOR_DISTANCE('dot', …)</c> / a registered function — all three return the <em>negative</em> inner product, which is what keeps it a distance</description></item>
/// </list>
/// </remarks>
public enum DistanceFunction
{
    /// <summary>
    /// Cosine distance, <c>1 − cos θ</c>, in <c>[0, 2]</c>. The default for text embeddings, whose
    /// models are trained for cosine similarity and usually return unit vectors.
    /// </summary>
    Cosine,

    /// <summary>
    /// Euclidean (L2) distance. Sensitive to magnitude; appropriate when the embedding model says so.
    /// </summary>
    L2,

    /// <summary>
    /// Negative inner product. Equivalent to cosine for unit vectors and cheaper to compute; only
    /// meaningful as a ranking when the vectors are normalised.
    /// </summary>
    InnerProduct
}
