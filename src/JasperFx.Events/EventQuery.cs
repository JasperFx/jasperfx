namespace JasperFx.Events;

public class EventQuery
{
    public string? EventTypeName { get; set; }
    public string? StreamId { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Optional exact-match filter on the event's correlation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the correlation id metadata column. See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Optional exact-match filter on the event's causation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the causation id metadata column. See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? CausationId { get; set; }

    /// <summary>
    /// Optional exact-match filter on the event's user name metadata. Null applies no filter. Only honored
    /// when the store advertises and captures the user name metadata column (cross-engine). See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? UserName { get; set; }
}

public class PagedEvents
{
    public IReadOnlyList<IEvent> Events { get; set; } = [];
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
}
