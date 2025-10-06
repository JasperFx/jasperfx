using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests;

public class ShardNameTests
{
    [Fact]
    public void default_key_and_version()
    {
        var name = new ShardName("Foo");
        name.Name.ShouldBe("Foo");
        name.ShardKey.ShouldBe(ShardName.All);
        name.Version.ShouldBe((uint)1);
        name.Identity.ShouldBe("Foo:All");
    }

    [Theory]
    [InlineData("foo", "All", 1, "foo/all")]
    [InlineData("foo", "All", 2, "foo/all/v2")]
    [InlineData("foo", "All", 3, "foo/all/v3")]
    public void relative_url(string name, string key, uint version, string expected)
    {
        var shardName = new ShardName(name, key, version);
        shardName.RelativeUrl.ShouldBe(expected);
    }
    
    [Theory]
    [InlineData("foo", "All", 1, "foo:All")]
    [InlineData("foo", "All", 2, "foo:V2:All")]
    [InlineData("foo", "All", 3, "foo:V3:All")]
    public void identity(string name, string key, uint version, string expected)
    {
        var shardName = new ShardName(name, key, version);
        shardName.Identity.ShouldBe(expected);
    }

    [Fact]
    public void identifier_for_different_key_and_version_is_one()
    {
        var name = new ShardName("Foo", "Other", 1u);
        name.Identity.ShouldBe("Foo:Other");
        name.ShardKey.ShouldBe("Other");
    }

    [Fact]
    public void identifier_for_more_than_version_1()
    {
        var name = new ShardName("Foo", "Other", 2);
        name.Identity.ShouldBe("Foo:V2:Other");
    }
    
    [Fact]
    public void clone_for_database()
    {
        var name = new ShardName("Foo", "Other", 2);
        var database = new Uri("postgresql://server1/db1/schema1");
        var clone = name.CloneForDatabase(database);
        
        clone.ShouldNotBeSameAs(name);
        clone.Database.ShouldBe(database);
        clone.Name.ShouldBe("Foo");
        clone.ShardKey.ShouldBe("Other");
        clone.Version.ShouldBe((uint)2);
    }
    
    
}