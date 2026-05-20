using JasperFx;
using JasperFx.MultiTenancy;
using Shouldly;

namespace CoreTests;

// The underlying int values of these lifted enums are part of the compatibility
// contract: downstream stores (Marten/Polecat) and any persisted configuration may
// depend on the ordinals. Reordering would silently shift them, so pin them here.
public class LiftedEnumValueTests
{
    [Fact]
    public void tenancy_style_ordinals()
    {
        ((int)TenancyStyle.Single).ShouldBe(0);
        ((int)TenancyStyle.Conjoined).ShouldBe(1);
    }

    [Fact]
    public void delete_style_ordinals()
    {
        ((int)DeleteStyle.Remove).ShouldBe(0);
        ((int)DeleteStyle.SoftDelete).ShouldBe(1);
    }
}
