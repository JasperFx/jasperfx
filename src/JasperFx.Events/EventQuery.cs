namespace JasperFx.Events;

public class EventQuery
{
    public string? EventTypeName { get; set; }
    public string? StreamId { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class PagedEvents
{
    public IReadOnlyList<IEvent> Events { get; set; } = [];
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
}
