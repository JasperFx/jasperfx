namespace Widgets1;

public abstract class WidgetMaker
{
    public abstract IWidget MakeWidget();
}

public class ColorWidgetMaker : WidgetMaker
{
    public ColorWidgetMaker(string color)
    {
        Color = color;
    }

    public string Color { get; }

    public override IWidget MakeWidget()
    {
        return null;
    }
}

public class MoneyWidgetMaker : WidgetMaker
{
    public double Amount { get; set; }

    public override IWidget MakeWidget()
    {
        return null;
    }
}