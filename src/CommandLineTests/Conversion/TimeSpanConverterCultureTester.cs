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
            TimeSpanConverter.GetTimeSpan("00:00:01.5").ShouldBe(TimeSpan.FromSeconds(1.5));
            TimeSpanConverter.GetTimeSpan("1.02:03:04.5").ShouldBe(new TimeSpan(1, 2, 3, 4, 500));
        });

        inGerman(() =>
        {
            TimeSpanConverter.GetTimeSpan("00:00:01.5").ShouldBe(TimeSpan.FromSeconds(1.5));
        });
    }

    [Fact]
    public void a_comma_in_a_clock_style_string_is_not_read_as_a_decimal_separator_in_any_culture()
    {
        // Read in the current culture, "00:00:01,5" was 1.5 seconds on a de-DE machine
        // and a FormatException on an en-US one
        inGerman(() =>
        {
            Should.Throw<FormatException>(() => TimeSpanConverter.GetTimeSpan("00:00:01,5"));
        });
    }
}
