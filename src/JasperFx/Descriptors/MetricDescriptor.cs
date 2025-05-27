namespace JasperFx.Descriptors;

public enum MetricsType
{
    Histogram,
    Counter,
    ObservableGauge
}

public class ActivitySpanDescriptor
{
    public string Name { get; }

    public ActivitySpanDescriptor(string name)
    {
        Name = name;
    }
    
    private readonly List<string> _tags = new();

    public void AddTag(string tagName)
    {
        _tags.Add(tagName);
    }
    
    public string[] Tags
    {
        get
        {
            return _tags.ToArray();
        }
        set
        {
            _tags.Clear();
            _tags.AddRange(value);
        }
    }

    public bool HasMultipleTenants { get; set; }
}

public class MetricDescriptor
{
    public string Name { get; }
    public MetricsType Type { get; }

    public MetricDescriptor(string name, MetricsType type)
    {
        Name = name;
        Type = type;
    }

    public string Units { get; set; } = "Number";

    public DatabaseCardinality DatabaseCardinality { get; set; } = DatabaseCardinality.None;

    private readonly List<string> _tags = new();

    public void AddTag(string tagName)
    {
        _tags.Add(tagName);
    }
    
    public string[] Tags
    {
        get
        {
            return _tags.ToArray();
        }
        set
        {
            _tags.Clear();
            _tags.AddRange(value);
        }
    }

    public bool HasMultipleTenants { get; set; }
}