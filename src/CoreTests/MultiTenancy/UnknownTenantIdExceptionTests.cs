using JasperFx.MultiTenancy;
using Shouldly;

namespace CoreTests.MultiTenancy;

public class UnknownTenantIdExceptionTests
{
    [Fact]
    public void exposes_tenant_id_property()
    {
        // Regression for jasperfx#224 (multi-tenancy dedup slice). The exception
        // previously embedded the tenant id in the message string only; consumers
        // had to parse it back out. The TenantId property is the diagnostics
        // surface that Polecat's now-removed local UnknownTenantException carried
        // and that downstream callers expect when consuming the canonical
        // JasperFx version.
        var ex = new UnknownTenantIdException("acme-east");

        ex.TenantId.ShouldBe("acme-east");
        ex.Message.ShouldContain("acme-east");
    }

    [Fact]
    public void the_message_names_a_remedy()
    {
        // jasperfx#874: "Unknown tenant id 'acme'" leaves the reader to guess between a tenant that
        // was never registered, one registered under a different casing, and (on Marten and Polecat)
        // one that exists and was deliberately disabled.
        var message = new UnknownTenantIdException("acme-east").Message;

        message.ShouldStartWith("Unknown tenant id 'acme-east'.");
        message.ShouldContain("Register the tenant with the tenancy source this store uses");
        message.ShouldContain("TenantIdStyle");
        message.ShouldContain("disabled tenant");
    }

    [Fact]
    public void a_source_that_knows_its_tenants_can_list_them()
    {
        var ex = new UnknownTenantIdException("acme-east", new[] { "two", "one", "three" });

        // Sorted, because the point is to let someone scan for the id they expected.
        ex.Message.ShouldContain("It knows: one, three, two.");
        ex.TenantId.ShouldBe("acme-east");
    }

    [Fact]
    public void a_source_that_cannot_enumerate_its_tenants_says_nothing_about_them()
    {
        // Sharded and dynamic sources would have to hit the database to answer, so they pass null
        // and the message is simply the fact plus the remedy.
        new UnknownTenantIdException("acme-east", null).Message.ShouldNotContain("It knows");
        new UnknownTenantIdException("acme-east", Array.Empty<string>()).Message.ShouldNotContain("It knows");
    }
}
