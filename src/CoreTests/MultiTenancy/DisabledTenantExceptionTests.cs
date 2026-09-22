using JasperFx.MultiTenancy;
using Shouldly;

namespace CoreTests.MultiTenancy;

public class DisabledTenantExceptionTests
{
    [Fact]
    public void says_the_tenant_is_there_and_how_to_bring_it_back()
    {
        // jasperfx#875: the whole point is that this is NOT "unknown". An operator who just ran
        // disable_tenant has to be able to tell the two apart.
        var ex = new DisabledTenantException("acme");

        ex.TenantId.ShouldBe("acme");
        ex.Message.ShouldBe(
            "Tenant 'acme' is registered but disabled, so this store will not open a session for it. Re-enable it through the tenancy source that owns it; its data is untouched.");
        ex.Message.ShouldNotContain("Unknown");
    }

    [Fact]
    public void is_still_caught_as_an_unknown_tenant()
    {
        // Deriving is the compatible choice: Marten and Polecat report disabled tenants as
        // UnknownTenantIdException today, so anything catching that keeps working when they adopt
        // this type.
        var ex = new DisabledTenantException("acme");

        ex.ShouldBeAssignableTo<UnknownTenantIdException>();

        Should.Throw<UnknownTenantIdException>(() => throw new DisabledTenantException("acme"))
            .TenantId.ShouldBe("acme");
    }

    [Fact]
    public void a_store_subclass_can_keep_its_own_wording()
    {
        var ex = new FisherishDisabledTenantException("acme");

        ex.Message.ShouldContain("Its database file is untouched");
        ex.TenantId.ShouldBe("acme");
        ex.ShouldBeAssignableTo<DisabledTenantException>();
    }

    private class FisherishDisabledTenantException : DisabledTenantException
    {
        public FisherishDisabledTenantException(string tenantId)
            : base(
                $"Tenant '{tenantId}' is registered but suspended, so this store will not open a session for it. Resume it through the ITenantSource that owns it. Its database file is untouched — Fisher never deletes a tenant's data.",
                tenantId)
        {
        }
    }
}
