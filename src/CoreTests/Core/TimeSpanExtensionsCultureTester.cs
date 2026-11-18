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

    private static void inGerman(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void clock_style_strings_are_read_with_the_invariant_culture()
    {
        inTurkish(() =>
        {
            "00:00:01.5".ToTime().ShouldBe(TimeSpan.FromSeconds(1.5));
            "1.02:03:04.5".ToTime().ShouldBe(new TimeSpan(1, 2, 3, 4, 500));
        });

        inGerman(() =>
        {
            "00:00:01.5".ToTime().ShouldBe(TimeSpan.FromSeconds(1.5));
        });
    }

    [Fact]
    public void a_comma_in_a_clock_style_string_is_not_read_as_a_decimal_separator_in_any_culture()
    {
        // Read in the current culture, "00:00:01,5" was 1.5 seconds on a de-DE machine
        // and a FormatException on an en-US one
        inGerman(() =>
        {
            Should.Throw<FormatException>(() => "00:00:01,5".ToTime());
        });
    }
}
