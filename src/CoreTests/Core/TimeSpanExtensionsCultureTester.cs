using System.Globalization;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class TimeSpanExtensionsCultureTester
{
    // tr-TR writes the decimal separator as a comma
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
    public void decimal_quantities_do_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "3.5s".ToTime().ShouldBe(TimeSpan.FromSeconds(3.5));
            "12.5 hours".ToTime().ShouldBe(TimeSpan.FromHours(12.5));
        });
    }
}
