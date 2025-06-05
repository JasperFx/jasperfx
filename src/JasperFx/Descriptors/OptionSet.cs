namespace JasperFx.Descriptors;

public class OptionSet
{
    public required string Subject { get; set; }
    public string[] SummaryColumns { get; set; } = [];
    public List<OptionsDescription> Rows { get; set; } = new();
}