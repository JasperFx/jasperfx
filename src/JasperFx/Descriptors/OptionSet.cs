namespace JasperFx.Descriptors;

public enum SetRenderMode
{
    Table,
    Tree
}

public class OptionSet
{
    public SetRenderMode RenderMode { get; set; } = SetRenderMode.Table;
    public required string Subject { get; set; }
    public string[] SummaryColumns { get; set; } = [];
    public List<OptionsDescription> Rows { get; set; } = new();
}