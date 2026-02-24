using System.Text.Json;
using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

public class VersionJsonConverterTests
{
    [Fact]
    public void round_trip_version_through_json()
    {
        var original = new Version(1, 2, 3, 4);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new VersionJsonConverter());

        var json = JsonSerializer.Serialize(original, options);
        json.ShouldBe("\"1.2.3.4\"");

        var deserialized = JsonSerializer.Deserialize<Version>(json, options);
        deserialized.ShouldBe(original);
    }

    [Fact]
    public void round_trip_assembly_descriptor()
    {
        var original = new AssemblyDescriptor("MyAssembly", new Version(3, 1, 0, 0));

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AssemblyDescriptor>(json);

        deserialized.ShouldNotBeNull();
        deserialized.Name.ShouldBe("MyAssembly");
        deserialized.Version.ShouldBe(new Version(3, 1, 0, 0));
    }

    [Fact]
    public void round_trip_assembly_descriptor_in_list()
    {
        var original = new List<AssemblyDescriptor>
        {
            new("First", new Version(1, 0, 0, 0)),
            new("Second", new Version(2, 5, 3, 1))
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<List<AssemblyDescriptor>>(json);

        deserialized.ShouldNotBeNull();
        deserialized.Count.ShouldBe(2);
        deserialized[0].Version.ShouldBe(new Version(1, 0, 0, 0));
        deserialized[1].Version.ShouldBe(new Version(2, 5, 3, 1));
    }

    [Fact]
    public void round_trip_null_version()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new VersionJsonConverter());

        var json = JsonSerializer.Serialize<Version?>(null, options);
        var deserialized = JsonSerializer.Deserialize<Version?>(json, options);

        deserialized.ShouldBeNull();
    }
}
