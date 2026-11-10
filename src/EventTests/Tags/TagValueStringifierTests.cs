using System.Globalization;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

public class TagValueStringifierTests
{
    /// <summary>
    /// A culture nothing ships with, so these tests do not depend on which cultures the machine has:
    /// a negative sign and a decimal separator that the invariant form never contains.
    /// </summary>
    private static CultureInfo HostileCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberDecimalSeparator = "|";
        return culture;
    }

    private static string StringifyUnder(CultureInfo culture, object value)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;

        try
        {
            return TagValueStringifier.Stringify(value);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void a_string_passes_through()
    {
        TagValueStringifier.Stringify("Student-1").ShouldBe("Student-1");
    }

    [Fact]
    public void a_guid_is_lowercase_and_hyphenated()
    {
        // These rows are already persisted in mt_dcb_tag_version and pc_dcb_tag_version, so this pins the
        // literal text rather than comparing against whatever Guid.ToString() happens to do.
        TagValueStringifier.Stringify(Guid.Parse("0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9"))
            .ShouldBe("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");
    }

    [Theory]
    [InlineData(-42, "-42")]
    [InlineData(-42L, "-42")]
    [InlineData((short)-42, "-42")]
    [InlineData(1.5d, "1.5")]
    public void a_number_does_not_change_with_the_culture(object value, string expected)
    {
        // The check on the arrangement: plain ToString() does change here, so the assertion below is
        // not passing because the culture had no effect. That is what Fisher's boundary was doing.
        var culture = HostileCulture();
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        var unstable = value.ToString();
        CultureInfo.CurrentCulture = original;
        unstable.ShouldNotBe(expected);

        StringifyUnder(culture, value).ShouldBe(expected);
    }

    [Fact]
    public void a_value_that_is_not_formattable_uses_to_string()
    {
        TagValueStringifier.Stringify(true).ShouldBe("True");
    }

    [Fact]
    public void a_to_string_that_answers_null_becomes_empty()
    {
        TagValueStringifier.Stringify(new AnswersNull()).ShouldBe(string.Empty);
    }

    [Fact]
    public void null_is_refused()
    {
        Should.Throw<ArgumentNullException>(() => TagValueStringifier.Stringify(null!));
    }

    private class AnswersNull
    {
        public override string? ToString() => null;
    }
}
