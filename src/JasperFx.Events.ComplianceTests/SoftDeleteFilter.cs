using System;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// One of the four soft-delete LINQ operators every store ships under its own name:
/// <c>MaybeDeleted()</c>, <c>IsDeleted()</c>, <c>DeletedSince(t)</c> and <c>DeletedBefore(t)</c>
/// (jasperfx#785).
/// </summary>
public enum SoftDeleteOperator
{
    /// <summary>Live and soft-deleted rows alike.</summary>
    MaybeDeleted,

    /// <summary>Only soft-deleted rows.</summary>
    IsDeleted,

    /// <summary>Rows soft-deleted after <see cref="SoftDeleteFilter.At" />.</summary>
    DeletedSince,

    /// <summary>Rows soft-deleted before <see cref="SoftDeleteFilter.At" />.</summary>
    DeletedBefore
}

/// <summary>
/// What <see cref="DocumentStorageComplianceFixture.QuerySoftDeletedAsync{T}" /> asks the store to
/// apply: one soft-delete operator, through the store's own marker method, in a single <c>Where()</c>.
/// </summary>
/// <remarks>
/// A value rather than an expression because each store declares its own <c>SoftDeletedExtensions</c>
/// and recognizes the marker by its declaring type, so an expression written in shared source would
/// carry the wrong <c>MethodInfo</c> and never be recognized: <c>DcbHasTagLinqCompliance</c>'s problem
/// verbatim. The fixture turns this into the store's own predicate, so the store's parser is the
/// thing under test.
/// </remarks>
public sealed record SoftDeleteFilter(SoftDeleteOperator Operator, DateTimeOffset? At = null)
{
    public static SoftDeleteFilter MaybeDeleted { get; } = new(SoftDeleteOperator.MaybeDeleted);

    public static SoftDeleteFilter IsDeleted { get; } = new(SoftDeleteOperator.IsDeleted);

    public static SoftDeleteFilter DeletedSince(DateTimeOffset at) => new(SoftDeleteOperator.DeletedSince, at);

    public static SoftDeleteFilter DeletedBefore(DateTimeOffset at) => new(SoftDeleteOperator.DeletedBefore, at);
}
