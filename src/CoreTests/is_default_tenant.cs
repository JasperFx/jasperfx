using JasperFx;
using NSubstitute;
using Shouldly;

namespace CoreTests;

public class is_default_tenant
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("blue", false)]
    public void has_default_tenant_id(string value, bool isDefault)
    {
        var hasTenant = Substitute.For<IHasTenantId>();
        hasTenant.TenantId.Returns(value);
        hasTenant.IsDefaultTenant().ShouldBe(isDefault);
    }
}