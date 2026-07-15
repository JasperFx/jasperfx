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

    [Fact]
    public void the_port_is_null_when_nobody_sets_it()
    {
        // Every descriptor built before the property existed. Consumers have to treat the port as
        // unknown rather than assuming a default.
        new DatabaseDescriptor(this) { Engine = "postgresql", ServerName = "server1" }
            .Port.ShouldBeNull();
    }

    [Fact]
    public void the_port_distinguishes_co_hosted_servers()
    {
        // ServerName is the host alone, so without the port two clusters on one box are the same
        // descriptor — and anything keyed on the server (a connection budget, say) collides them.
        var first = new DatabaseDescriptor(this)
        {
            Engine = "postgresql", ServerName = "localhost", Port = 5432, DatabaseName = "db1"
        };

        var second = new DatabaseDescriptor(this)
        {
            Engine = "postgresql", ServerName = "localhost", Port = 5433, DatabaseName = "db1"
        };

        first.ShouldNotBe(second);
        first.GetHashCode().ShouldNotBe(second.GetHashCode());
    }

    [Fact]
    public void the_port_does_not_change_the_database_uri()
    {
        // DatabaseUri is load-bearing as an identity elsewhere (agent URIs, database ids). Folding
        // a port segment into it would silently rename every existing database.
        var descriptor = new DatabaseDescriptor(this)
        {
            Engine = "postgresql", ServerName = "server1", Port = 5432, DatabaseName = "db1"
        };

        descriptor.DatabaseUri().ShouldBe(new Uri("postgresql://server1/db1"));
    }

    [Fact]
    public void a_descriptor_carrying_a_port_is_still_serializable()
    {
        new DatabaseDescriptor(this)
        {
            Engine = "postgresql", ServerName = "server1", Port = 5432, DatabaseName = "db1"
        }.ShouldBeSerializable();
    }
}