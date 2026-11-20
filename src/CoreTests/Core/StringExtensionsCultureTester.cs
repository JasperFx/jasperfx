using System.Globalization;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class StringExtensionsCultureTester
{
    // tr-TR and az-AZ lower an uppercase I to a dotless ı
    private static void inTurkish(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void table_alias_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "InvoiceIssued".ToTableAlias().ShouldBe("invoice_issued");
            "IsActive".ToTableAlias().ShouldBe("is_active");
        });
    }
}
