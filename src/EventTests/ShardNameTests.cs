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

    [Fact]
    public void null_tenant_keeps_identity_and_url_unchanged()
    {
        // The whole grammar change is additive: a null tenant must look exactly like today.
        var name = ShardName.Compose("Foo", "All");
        name.TenantId.ShouldBeNull();
        name.Identity.ShouldBe("Foo:All");
        name.RelativeUrl.ShouldBe("foo/all");

        var versioned = ShardName.Compose("Foo", "All", version: 2);
        versioned.TenantId.ShouldBeNull();
        versioned.Identity.ShouldBe("Foo:V2:All");
        versioned.RelativeUrl.ShouldBe("foo/all/v2");
    }

    [Fact]
    public void compose_with_tenant_appends_distinct_trailing_slot()
    {
        var name = ShardName.Compose("Foo", "All", "tenant1");
        name.TenantId.ShouldBe("tenant1");
        name.ShardKey.ShouldBe("All"); // tenant is NOT folded into the shard key
        name.Identity.ShouldBe("Foo:All:tenant1");
        name.RelativeUrl.ShouldBe("foo/all/tenant1");

        var versioned = ShardName.Compose("Foo", "All", "tenant1", 2);
        versioned.Identity.ShouldBe("Foo:V2:All:tenant1");
        versioned.RelativeUrl.ShouldBe("foo/all/v2/tenant1");
    }

    [Fact]
    public void compose_treats_empty_tenant_as_store_global()
    {
        ShardName.Compose("Foo", "All", "").TenantId.ShouldBeNull();
        ShardName.Compose("Foo", "All", "").Identity.ShouldBe("Foo:All");
    }

    [Theory]
    [InlineData("Foo", "All", null, 1u)]      // 2-segment
    [InlineData("Foo", "Other", null, 1u)]    // 2-segment, custom key
    [InlineData("Foo", "All", "tenant1", 1u)] // 3-segment tenant form
    [InlineData("Foo", "All", null, 2u)]      // 3-segment version form
    [InlineData("Foo", "All", "tenant1", 3u)] // 4-segment version + tenant
    [InlineData("Foo", "Other", "acme", 2u)]
    public void try_parse_round_trips_compose(string name, string key, string? tenant, uint version)
    {
        var composed = ShardName.Compose(name, key, tenant, version);

        ShardName.TryParse(composed.Identity, out var parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed!.Name.ShouldBe(name);
        parsed.ShardKey.ShouldBe(key);
        parsed.TenantId.ShouldBe(tenant);
        parsed.Version.ShouldBe(version);
        parsed.Identity.ShouldBe(composed.Identity);
        parsed.ShouldBe(composed); // equality is identity-based
    }

    [Fact]
    public void try_parse_round_trips_high_water_mark()
    {
        ShardName.TryParse(ShardState.HighWaterMark, out var parsed).ShouldBeTrue();
        parsed!.Identity.ShouldBe(ShardState.HighWaterMark);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void try_parse_rejects_empty(string? text)
    {
        ShardName.TryParse(text, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Fact]
    public void for_tenant_rebinds_to_tenant_partition()
    {
        var global = ShardName.Compose("Foo", "All");
        var scoped = global.ForTenant("tenant1");

        scoped.TenantId.ShouldBe("tenant1");
        scoped.Identity.ShouldBe("Foo:All:tenant1");
        scoped.ForTenant(null).TenantId.ShouldBeNull();
    }
}