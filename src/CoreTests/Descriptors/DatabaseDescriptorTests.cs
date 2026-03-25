using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

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

    [Fact]
    public void derive_uri_with_unix_socket_path()
    {
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "postgresql",
            ServerName = "/cloudsql/platform-dev:europe-west4:shared-db",
            DatabaseName = "sandbox",
            SchemaOrNamespace = "public"
        };

        // Forward slashes and colons in the server name should be replaced with underscores
        var uri = descriptor.DatabaseUri();
        uri.ShouldBe(new Uri("postgresql://_cloudsql_platform-dev_europe-west4_shared-db/sandbox/public"));
    }

    [Fact]
    public void derive_uri_with_multi_host_pipeline()
    {
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "postgresql",
            ServerName = "host1,host2,host3",
            DatabaseName = "mydb"
        };

        // Should use only the first host
        var uri = descriptor.DatabaseUri();
        uri.ShouldBe(new Uri("postgresql://host1/mydb"));
    }

    [Fact]
    public void derive_uri_with_multi_host_pipeline_and_schema()
    {
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "postgresql",
            ServerName = "primary.example.com,replica.example.com",
            DatabaseName = "mydb",
            SchemaOrNamespace = "myschema"
        };

        var uri = descriptor.DatabaseUri();
        uri.ShouldBe(new Uri("postgresql://primary.example.com/mydb/myschema"));
    }

    [Fact]
    public void database_descriptor_is_serializable()
    {
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "sqlserver",
            ServerName = "server1",
            DatabaseName = "db1"
        };

        descriptor.ShouldBeSerializable();
    }
}