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
}
