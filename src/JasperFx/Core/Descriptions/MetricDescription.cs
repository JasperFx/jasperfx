namespace JasperFx.Core.Descriptions;

public enum MetricsType
{
    Histogram,
    Counter,
    ObservableGauge
}

public class MetricDescription
{
    public string Name { get; }
    public MetricsType Type { get; }

    public MetricDescription(string name, MetricsType type)
    {
        Name = name;
        Type = type;
    }

    public string Units { get; set; } = "Number";

    public Dictionary<string, string> Tags = new();
}