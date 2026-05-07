using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Shouldly;

namespace CodegenTests.Frames;

public class AppendActivityEventFrameTests
{
    [Fact]
    public void emits_addEvent_using_fully_qualified_type_names()
    {
        var frame = new AppendActivityEventFrame("my.event");
        var writer = new SourceWriter();

        frame.GenerateCode(GeneratedMethod.ForNoArg("Foo"), writer);

        var line = writer.Code().ReadLines().Single();
        line.ShouldBe(
            "System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent(\"my.event\"));");
    }

    [Fact]
    public void preserves_event_name_verbatim()
    {
        var frame = new AppendActivityEventFrame("wolverine.handler.started");

        frame.EventName.ShouldBe("wolverine.handler.started");
    }

    [Fact]
    public void rejects_null_event_name()
    {
        Should.Throw<ArgumentNullException>(() => new AppendActivityEventFrame(null!));
    }

    [Fact]
    public void chains_to_next_frame()
    {
        var frame = new AppendActivityEventFrame("first");
        frame.Next = new AppendActivityEventFrame("second");

        var writer = new SourceWriter();
        frame.GenerateCode(GeneratedMethod.ForNoArg("Foo"), writer);

        var lines = writer.Code().ReadLines().ToArray();
        lines.Length.ShouldBe(2);
        lines[0].ShouldContain("\"first\"");
        lines[1].ShouldContain("\"second\"");
    }

    [Fact]
    public void declares_no_variables()
    {
        var frame = new AppendActivityEventFrame("anything");
        frame.FindVariables(null!).ShouldBeEmpty();
    }
}
