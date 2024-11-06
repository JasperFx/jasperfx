namespace JasperFx;

public interface IOperationContext
{
    string TenantId { get; }

    /// <summary>
    ///     Optional metadata describing the causation id for this
    ///     unit of work
    /// </summary>
    string? CausationId { get; set; }

    /// <summary>
    ///     Optional metadata describing the correlation id for this
    ///     unit of work
    /// </summary>
    string? CorrelationId { get; set; }

    /// <summary>
    ///     Optional metadata describing the user name or
    ///     process name for this unit of work
    /// </summary>
    string? LastModifiedBy { get; set; }

    /// <summary>
    ///     Optional metadata values. This may be null.
    /// </summary>
    Dictionary<string, object>? Headers { get; }
}
