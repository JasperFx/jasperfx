using System.Text.Json;
using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

public class EntityDescriptorTests
{
    [Fact]
    public void ddl_defaults_to_empty()
    {
        new EntityDescriptor().Ddl.ShouldBe("");
    }

    [Fact]
    public void ddl_survives_a_json_round_trip()
    {
        var descriptor = new EntityDescriptor
        {
            TableName = "Orders",
            Ddl = "CREATE TABLE [Orders] ([Id] uniqueidentifier NOT NULL);"
        };

        var copy = JsonSerializer.Deserialize<EntityDescriptor>(JsonSerializer.Serialize(descriptor))!;

        copy.Ddl.ShouldBe(descriptor.Ddl);
    }
}
