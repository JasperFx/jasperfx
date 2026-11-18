using System.Globalization;
using JasperFx.CommandLine.Internal.Conversion;
using Shouldly;

namespace CommandLineTests.Conversion;

public class TimeSpanConverterCultureTester
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
            TimeSpanConverter.GetTimeSpan("3.5s").ShouldBe(TimeSpan.FromSeconds(3.5));
            TimeSpanConverter.GetTimeSpan("12.5 hours").ShouldBe(TimeSpan.FromHours(12.5));
        });
    }
}
