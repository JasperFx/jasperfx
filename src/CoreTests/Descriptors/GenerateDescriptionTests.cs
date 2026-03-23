using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

// Test target class — explicitly implements IDescribeMyself
public class SampleSettings : IDescribeMyself
{
    public string Name { get; set; } = "default";
    public int Count { get; set; } = 42;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public bool Enabled { get; set; } = true;
    public Uri? Endpoint { get; set; }
    public SampleMode Mode { get; set; } = SampleMode.Balanced;
    public string InternalOnly { get; set; } = "secret";

    public OptionsDescription ToDescription()
    {
        var desc = new OptionsDescription { Subject = "CoreTests.Descriptors.SampleSettings" };
        desc.AddValue(nameof(Name), Name);
        desc.AddValue(nameof(Count), Count);
        desc.AddValue(nameof(Interval), Interval);
        desc.AddValue(nameof(Enabled), Enabled);
        if (Endpoint != null) desc.AddValue(nameof(Endpoint), Endpoint);
        desc.AddValue(nameof(Mode), Mode);
        // InternalOnly intentionally excluded
        return desc;
    }
}

public enum SampleMode { Solo, Balanced, Serverless }

public class ExplicitDescriptionTests
{
    [Fact]
    public void explicit_description_has_correct_properties()
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

        var desc = settings.ToDescription();

        desc.Subject.ShouldBe("CoreTests.Descriptors.SampleSettings");

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
        (settings is IDescribeMyself).ShouldBeTrue();
    }
}
