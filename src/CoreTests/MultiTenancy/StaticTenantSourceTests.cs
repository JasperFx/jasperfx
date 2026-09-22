using JasperFx;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Shouldly;

namespace CoreTests.MultiTenancy;

public class StaticTenantSourceTests
{
    private readonly StaticConnectionStringSource theSource = new();

    public StaticTenantSourceTests()
    {
        theSource.Register("one", "Host=one");
        theSource.Register("two", "Host=two");
    }

    [Fact]
    public async Task finds_a_registered_tenant()
    {
        (await theSource.FindAsync("one")).ShouldBe("Host=one");
    }

    [Fact]
    public async Task unknown_tenant_throws_the_shared_exception()
    {
        // jasperfx#874: this used to be an ArgumentOutOfRangeException with the same words, so a
        // caller catching UnknownTenantIdException -- which is what every store throws for this --
        // missed the one tenancy source JasperFx ships itself.
        var ex = await Should.ThrowAsync<UnknownTenantIdException>(async () =>
            await theSource.FindAsync("three"));

        ex.TenantId.ShouldBe("three");
    }

    [Fact]
    public async Task unknown_tenant_message_names_what_the_source_does_know()
    {
        var ex = await Should.ThrowAsync<UnknownTenantIdException>(async () =>
            await theSource.FindAsync("three"));

        ex.Message.ShouldContain("It knows: one, two.");
    }

    [Fact]
    public async Task the_default_tenant_is_found_when_it_was_registered()
    {
        theSource.RegisterDefault("Host=default");

        (await theSource.FindAsync(StorageConstants.DefaultTenantId)).ShouldBe("Host=default");
    }

    [Fact]
    public void cardinality_is_static_multiple()
    {
        theSource.Cardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
    }
}
