using ImTools;
using JasperFx;
using JasperFx.MultiTenancy;
using Shouldly;

namespace CoreTests.MultiTenancy;

public class maybe_correct_tenant_id
{
    [Theory]
    [InlineData(TenantIdStyle.CaseSensitive, null, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceLowerCase, null, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceUpperCase, null, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.CaseSensitive, "", StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceLowerCase, "", StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceUpperCase, "", StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.CaseSensitive, StorageConstants.DefaultTenantId, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceLowerCase, StorageConstants.DefaultTenantId, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.ForceUpperCase, StorageConstants.DefaultTenantId, StorageConstants.DefaultTenantId)]
    [InlineData(TenantIdStyle.CaseSensitive, "One", "One")]
    [InlineData(TenantIdStyle.ForceLowerCase, "One", "one")]
    [InlineData(TenantIdStyle.ForceUpperCase, "One", "ONE")]
    public void correct_tenant_id(TenantIdStyle tenantIdStyle, string raw, string corrected)
    {
        tenantIdStyle.MaybeCorrectTenantId(raw).ShouldBe(corrected);
    }
}