using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

public class OptionsDescriptionTests
{
    [Fact]
    public void serializable()
    {
        var description = new OptionsDescription();
        description.AddValue("Foo", 1);
        var children = new OptionSet() { Subject = ""};
        description.Sets["Children"] = children;
        children.SummaryColumns = ["a", "b"];
        children.Rows.Add(new OptionsDescription());
        
        description.ShouldBeSerializable();
    }

    [Fact]
    public void pick_up_tags_from_itagged()
    {
        var description = new OptionsDescription(new SomeObject());
        description.Tags.ShouldContain("blue");
        description.Tags.ShouldContain("green");
    }
}

public class SomeObject : ITagged
{
    public string[] Tags => ["blue", "green"];
    public string Name { get; set; } = "Gambit";
}