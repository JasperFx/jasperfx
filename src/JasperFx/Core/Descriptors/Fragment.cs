using JasperFx.CommandLine.TextualDisplays;
using Spectre.Console;

namespace JasperFx.Core.Descriptors;

public abstract class Fragment 
{
    public bool Italic { get; set; }
    public bool Bold { get; set; }
    public HighlightMode Highlight { get; set; } = HighlightMode.None;
    public Justify TextAlign { get; set; } = Justify.Left;

    public Markup WrapText(string text)
    {
        var prefix = string.Empty;
        if (Italic) prefix += " italic";
        if (Bold) prefix += " bold";

        switch (Highlight)
        {
            case HighlightMode.Success:
                prefix += " green";
                break;
            case HighlightMode.Fail:
                prefix += " red";
                break;
            case HighlightMode.Warning:
                prefix += " yellow";
                break;
        }

        if (prefix.IsEmpty()) return new Markup(text.EscapeMarkup());

        return new Markup($"[{prefix.Trim()}]{text.EscapeMarkup()}[/]");
    }
}