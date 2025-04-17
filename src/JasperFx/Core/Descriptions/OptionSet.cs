namespace JasperFx.Core.Descriptions;

public class OptionSet
{
    public string Subject { get; set; }
    public string[] SummaryColumns { get; set; } = Array.Empty<string>();
    public List<OptionsDescription> Rows { get; set; } = new();
}