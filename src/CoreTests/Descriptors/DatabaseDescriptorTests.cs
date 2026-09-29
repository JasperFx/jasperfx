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

    [Theory]
    [InlineData(@"db-host\MSSQL2017", "sqlserver://db-host_mssql2017/mydb")]
    [InlineData(@".\SQLEXPRESS", "sqlserver://__sqlexpress/mydb")]
    [InlineData(@"(localdb)\MSSQLLocalDB", "sqlserver://_localdb__mssqllocaldb/mydb")]
    [InlineData(@"tcp:db-host\MSSQL2017,1433", "sqlserver://tcp_db-host_mssql2017/mydb")]
    public void derive_uri_from_a_sql_server_named_instance(string dataSource, string expected)
    {
        // GH-918. Every one of these threw "Invalid URI: The hostname could not be parsed", because
        // the server name was percent-escaped into the host position and a percent-escape is not
        // legal there. A named instance is the reported case; SQL Express and LocalDB came with it.
        new DatabaseDescriptor(this) { Engine = "sqlserver", ServerName = dataSource, DatabaseName = "mydb" }
            .DatabaseUri().ShouldBe(new Uri(expected));
    }

    [Fact]
    public void every_printable_ascii_character_yields_a_uri()
    {
        // GH-918. The point of sanitizing against the permitted set rather than replacing offenders
        // one report at a time: this method had already grown a comma split and a '/' + ':' rule,
        // and 27 characters were still left to be discovered the hard way.
        // Every position, not just the middle one: a character can be legal between two letters and
        // still be refused at the front, which is how ".\SQLEXPRESS" survived the first draft of
        // this fix.
        for (var c = (char)32; c < (char)127; c++)
        {
            foreach (var serverName in new[] { $"a{c}b", $"{c}ab", $"ab{c}", c.ToString(), $"a{c}{c}b" })
            {
                var descriptor = new DatabaseDescriptor(this)
                {
                    Engine = "sqlserver", ServerName = serverName, DatabaseName = "mydb"
                };

                Should.NotThrow(() => descriptor.DatabaseUri(),
                    $"Server name '{serverName}' (char {(int)c})");
            }
        }
    }

    [Fact]
    public void a_lone_dot_server_name_is_left_alone()
    {
        // "." is SQL Server's local default instance and a legal URI host, so the dot-folding
        // fallback must not reach it. This is why that fallback is conditional on the URI having
        // actually failed to build rather than applied up front.
        new DatabaseDescriptor(this) { Engine = "sqlserver", ServerName = ".", DatabaseName = "mydb" }
            .DatabaseUri().ShouldBe(new Uri("sqlserver://./mydb"));
    }

    [Fact]
    public void sanitizing_the_host_renames_nothing_that_works_today()
    {
        // GH-918. DatabaseUri is load-bearing as an identity (agent URIs, database ids), so the
        // bar for touching it is that no working deployment is renamed. A server name only ever
        // reached the host unchanged if every character was already in the permitted set, so the
        // inputs this changes are exactly the inputs that used to throw. Assert that directly:
        // every character that produced a URI before must produce the SAME URI now.
        foreach (var c in "-._0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")
        {
            var descriptor = new DatabaseDescriptor(this)
            {
                Engine = "postgresql", ServerName = $"a{c}b", DatabaseName = "mydb"
            };

            descriptor.DatabaseUri().ShouldBe(new Uri($"postgresql://a{c}b/mydb".ToLowerInvariant()));
        }
    }

    [Fact]
    public void a_descriptor_with_no_server_name_is_still_named_after_its_database()
    {
        // The host is "the first non-empty part", not "the server name" — dropping the empties
        // before choosing it is what keeps this shape working.
        new DatabaseDescriptor(this) { Engine = "ravendb", DatabaseName = "db1", SchemaOrNamespace = "schema1" }
            .DatabaseUri().ShouldBe(new Uri("ravendb://db1/schema1"));
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