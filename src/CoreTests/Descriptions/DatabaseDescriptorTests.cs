using JasperFx.Core.Descriptions;
using Shouldly;

namespace CoreTests.Descriptions;

public class DatabaseDescriptorTests
{
    [Fact]
    public void derive_uri()
    {
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "sqlserver",
            ServerName = "server1",
            DatabaseName = "db1"
        };
        
        descriptor.DatabaseUri().ShouldBe(new Uri("sqlserver://server1/db1"));

        descriptor.SchemaOrNamespace = "schema1";
        
        descriptor.DatabaseUri().ShouldBe(new Uri("sqlserver://server1/db1/schema1"));
    }
}