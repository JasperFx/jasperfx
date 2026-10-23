namespace Internal.Generated.Widgets;

/// <summary>
/// Stands in for pre-generated code (the output of <c>codegen write</c>): nothing references it statically,
/// so the only things keeping it alive under Native AOT are the AOT roots companion beside it and
/// the static-mode lookup that finds it.
/// </summary>
public sealed class WidgetMaker : JasperFx.AotRootsSmoke.IWidgetMaker
{
    public int Make() => 42;
}
