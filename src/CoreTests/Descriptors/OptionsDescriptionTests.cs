using JasperFx.Descriptors;

namespace CoreTests.Descriptors;

public class OptionsDescriptionTests
{
    [Fact]
    public void serializable()
    {
        var description = new OptionsDescription();
        description.AddValue("Foo", 1);
        var children = new OptionSet();
        description.Sets["Children"] = children;
        children.SummaryColumns = ["a", "b"];
        children.Rows.Add(new OptionsDescription());
        
        description.ShouldBeSerializable();
    }
}