using JasperFx.MultiTenancy;
using NSubstitute;
using Shouldly;

namespace CoreTests.MultiTenancy;

public class is_default_tenant
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("blue", false)]
    public void has_default_tenant_id(string value, bool isDefault)
    {
        var hasTenant = new StubHasTenantId { TenantId = value };
        hasTenant.IsDefaultTenant().ShouldBe(isDefault);
    }
}

public class StubHasTenantId : IHasTenantId
{
    public string? TenantId { get; set; }
}