using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

// Test target class — the source generator should emit ToDescription()
[GenerateDescription]
public partial class SampleSettings
{
    public string Name { get; set; } = "default";
    public int Count { get; set; } = 42;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public bool Enabled { get; set; } = true;
    public Uri? Endpoint { get; set; }
    public SampleMode Mode { get; set; } = SampleMode.Balanced;

    [IgnoreDescription]
    public string InternalOnly { get; set; } = "secret";
}

public enum SampleMode { Solo, Balanced, Serverless }

public class GenerateDescriptionTests
{
    [Fact]
    public void generated_description_has_correct_properties()
    {
        var settings = new SampleSettings
        {
            Name = "TestService",
            Count = 100,
            Interval = TimeSpan.FromMinutes(5),
            Enabled = false,
            Endpoint = new Uri("tcp://localhost:5000"),
            Mode = SampleMode.Serverless,
            InternalOnly = "should not appear"
        };

        // This should compile thanks to the source generator emitting ToDescription()
        var desc = settings.ToDescription();

        desc.Subject.ShouldBe("CoreTests.Descriptors.SampleSettings");

        // Check each property type classification
        var name = desc.PropertyFor("Name");
        name.ShouldNotBeNull();
        name!.Value.ShouldBe("TestService");
        name.Type.ShouldBe(PropertyType.Text);

        var count = desc.PropertyFor("Count");
        count.ShouldNotBeNull();
        count!.Value.ShouldBe("100");
        count.Type.ShouldBe(PropertyType.Numeric);

        var interval = desc.PropertyFor("Interval");
        interval.ShouldNotBeNull();
        interval!.Type.ShouldBe(PropertyType.TimeSpan);

        var enabled = desc.PropertyFor("Enabled");
        enabled.ShouldNotBeNull();
        enabled!.Value.ShouldBe("False");
        enabled.Type.ShouldBe(PropertyType.Boolean);

        var endpoint = desc.PropertyFor("Endpoint");
        endpoint.ShouldNotBeNull();
        endpoint!.Type.ShouldBe(PropertyType.Uri);

        var mode = desc.PropertyFor("Mode");
        mode.ShouldNotBeNull();
        mode!.Value.ShouldBe("Serverless");
        mode.Type.ShouldBe(PropertyType.Enum);

        // InternalOnly should NOT be in the description
        desc.PropertyFor("InternalOnly").ShouldBeNull();
    }

    [Fact]
    public void implements_IDescribeMyself()
    {
        var settings = new SampleSettings();

        // Verify the generated code implements the interface
        (settings is IDescribeMyself).ShouldBeTrue();
    }
}
