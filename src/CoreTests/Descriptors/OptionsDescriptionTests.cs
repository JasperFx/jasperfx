using System.Text.Json;
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

    [Fact]
    public void children_and_sets_round_trip_through_default_json_options()
    {
        // Regression for #203 — `Children` and `Sets` were public fields, not
        // properties. System.Text.Json with its default options walks properties
        // only, so every [ChildDescription] and every AddChildSet(...) call was
        // being silently dropped at the JSON boundary (e.g. Marten's
        // EventGraph.MetadataConfig was invisible in CritterWatch).
        var description = new OptionsDescription { Subject = "Parent" };

        var child = new OptionsDescription { Subject = "Child" };
        child.AddValue("Foo", 42);
        description.Children["Inner"] = child;

        var set = description.AddChildSet("Members");
        set.Rows.Add(new OptionsDescription { Subject = "Row1" });
        set.Rows.Add(new OptionsDescription { Subject = "Row2" });

        // Default options — no IncludeFields = true override. This is what
        // both Wolverine.SignalR (JsonSerializerOptions.Web) and TS-side
        // codegen in CritterWatch effectively use.
        var json = JsonSerializer.Serialize(description);
        var round = JsonSerializer.Deserialize<OptionsDescription>(json);

        round.ShouldNotBeNull();
        round.Children.ShouldContainKey("Inner");
        round.Children["Inner"].Subject.ShouldBe("Child");
        round.Sets.ShouldContainKey("Members");
        round.Sets["Members"].Rows.Count.ShouldBe(2);
        round.Sets["Members"].Rows[0].Subject.ShouldBe("Row1");
        round.Sets["Members"].Rows[1].Subject.ShouldBe("Row2");
    }
}

public class SomeObject : ITagged
{
    public string[] Tags => ["blue", "green"];
    public string Name { get; set; } = "Gambit";
}