namespace JasperFx.Events;

public class StreamState
{
    public Guid Id { get; set; }
    public string? Key { get; set; }
    public long Version { get; set; }
    public Type? AggregateType { get; set; }
    public DateTimeOffset LastTimestamp { get; set; }
    public DateTimeOffset Created { get; set; }
    public bool IsArchived { get; set; }
}
