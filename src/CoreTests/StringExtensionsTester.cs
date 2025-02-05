using System.Globalization;
using JasperFx.Core;
using Shouldly;

namespace CoreTests;

public class StringExtensionsTester
{
    [Fact]
    public void parent_directory()
    {
        var path = ".".ToFullPath();
        path.ParentDirectory().ShouldBe(Path.GetDirectoryName(path));
    }

    [Fact]
    public void parent_directory_ending_with_directory_separator()
    {
        var path = ".".ToFullPath();
        (path + Path.DirectorySeparatorChar).ParentDirectory().ShouldBe(Path.GetDirectoryName(path));
    }

    [Fact]
    public void if_not_null_positive()
    {
        string captured = null;
        Action<string> action = s => captured = s;

        "a".IfNotNull(action);

        captured.ShouldBe("a");
    }


    [Fact]
    public void combine_to_path_when_rooted()
    {
        var rooted = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory()), "here");
        rooted.CombineToPath("there").ShouldBe(rooted);
    }

    [Fact]
    public void combine_to_path_when_not_rooted()
    {
        "here".CombineToPath("there").ShouldBe(Path.Combine("there", "here"));
    }

    [Fact]
    public void is_empty()
    {
        string.Empty.IsEmpty().ShouldBeTrue();

        string nullString = null;
        nullString.IsEmpty().ShouldBeTrue();

        " ".IsEmpty().ShouldBeFalse();
        "something".IsEmpty().ShouldBeFalse();
    }

    [Fact]
    public void is_not_empty()
    {
        string.Empty.IsNotEmpty().ShouldBeFalse();

        string nullString = null;
        nullString.IsNotEmpty().ShouldBeFalse();

        " ".IsNotEmpty().ShouldBeTrue();
        "something".IsNotEmpty().ShouldBeTrue();
    }


    [Fact]
    public void converting_plain_text_line_returns_to_line_breaks()
    {
        const string plainText = "before\nmiddle\r\nafter";

        var textWithBreaks = plainText.ConvertCRLFToBreaks();

        textWithBreaks.ShouldBe(@"before<br/>middle<br/>after");
    }

    [Fact]
    public void numbers_with_commas_and_periods_should_be_valid()
    {
        var numbers = new[]
        {
            "1,000",
            "100.1",
            "1000.1",
            "1,000.1",
            "10,000.1",
            "100,000.1"
        };

        foreach (var number in numbers) number.IsValidNumber(new CultureInfo("en-us")).ShouldBeTrue();
    }

    [Fact]
    public void numbers_with_commas_and_periods_should_be_valid_in_european_culture()
    {
        var numbers = new[]
        {
            "1.000",
            "100,1",
            "1000,1",
            "1.000,1",
            "10.000,1",
            "100.000,1"
        };

        foreach (var number in numbers) number.IsValidNumber(new CultureInfo("de-DE")).ShouldBeTrue();
    }

    [Fact]
    public void numbers_should_be_invalid()
    {
        var numbers = new[]
        {
            "1,00",
            "100,1",
            "100,1.01",
            "A,Jun.K"
        };

        foreach (var number in numbers) number.IsValidNumber(new CultureInfo("en-us")).ShouldBeFalse();
    }

    [Fact]
    public void to_bool()
    {
        "true".ToBool().ShouldBeTrue();
        "True".ToBool().ShouldBeTrue();

        "false".ToBool().ShouldBeFalse();
        "False".ToBool().ShouldBeFalse();

        "".ToBool().ShouldBeFalse();

        string nullString = null;
        nullString.ToBool().ShouldBeFalse();
    }

    [Fact]
    public void read_lines_to_an_enumerable()
    {
        var text = @"a
b
c
d
e
";

        text.ReadLines().ShouldHaveTheSameElementsAs("a", "b", "c", "d", "e");
    }

    [Fact]
    public void read_lines_to_an_action()
    {
        var list = new List<string>();

        Action<string> action = x => list.Add(x);

        var text = @"a
b
c
d
e
";


        text.ReadLines(action);

        list.ShouldHaveTheSameElementsAs("a", "b", "c", "d", "e");
    }

    [Fact]
    public void to_hash_is_repeatable()
    {
        "something".ToHash().ShouldBe("something".ToHash());
        "else".ToHash().ShouldNotBe("something".ToHash());
    }

    [Fact]
    public void to_hash_produces_expected_format()
    {
        var somethingHash = "437b930db84b8079c2dd804a71936b5f";
        somethingHash.ShouldBe("something".ToHash());
    }


    [Fact]
    public void to_enum()
    {
        var x = "Machine".ToEnum<EnvTarget>();
        x.ShouldBe(EnvTarget.Machine);
    }

    [Fact]
    public void to_enum_should_ignore_case()
    {
        var x = "machine".ToEnum<EnvTarget>();
        x.ShouldBe(EnvTarget.Machine);
    }

    [Fact]
    public void to_enum_should_throw_if_not_enum()
    {
        Exception<ArgumentException>.ShouldBeThrownBy(() => { "a".ToEnum<NotAnEnum>(); });
    }

    [Fact]
    public void should_split_on_camel_case()
    {
        "camelCaseString".SplitCamelCase().ShouldBe("camel Case String");
    }

    [Fact]
    public void should_split_on_pascal_case()
    {
        "PascalCaseString".SplitPascalCase().ShouldBe("Pascal Case String");
    }

    [Fact]
    public void file_escape()
    {
        "my file".FileEscape().ShouldBe("\"my file\"");
    }

    [Fact]
    public void replace_first()
    {
        var original = "what the ? is ?";
        original.ReplaceFirst("?", "heck")
            .ShouldBe("what the heck is ?");
    }

    [Theory]
    [InlineData("Where are the cups?", "cups", true)]
    [InlineData("Where are the cups?", "Cups", true)]
    [InlineData("Where are the cups?", "glasses", false)]
    public void contains_ignore_case(string source, string value, bool expected)
    {
        source.ContainsIgnoreCase(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("FooBar", "fooBar")]
    [InlineData("Foo", "foo")]
    [InlineData("foo", "foo")]
    public void ToCamelCase(string original, string transformed)
    {
        original.ToCamelCase().ShouldBe(transformed);
    }

    [Theory]
    [InlineData("hello", 100, "hello")]
    [InlineData("hello how are you?", 18, "hello how are you?")]
    [InlineData("hello how are you?", 15, "hello how ar...")]
    public void elid_string(string raw, int length, string expected)
    {
        raw.Elid(length).ShouldBe(expected);
    }

    [Fact]
    public void stable_hash()
    {
        var str1 = Guid.NewGuid().ToString();
        var str2 = Guid.NewGuid().ToString();

        str1.GetStableHashCode().ShouldBe(str1.GetStableHashCode());
        str2.GetStableHashCode().ShouldBe(str2.GetStableHashCode());
        str1.GetStableHashCode().ShouldNotBe(str2.GetStableHashCode());
    }

    [Theory]
    [InlineData("foo", "bar", false)]
    [InlineData("foobar", "bar", false)]
    [InlineData("foobar", "foo", true)]
    [InlineData("Foobar", "foo", true)]
    [InlineData("Foobar", "fOo", true)]
    public void starts_with_ignore_case(string one, string prefix, bool expected)
    {
        one.StartsWithIgnoreCase(prefix).ShouldBe(expected);
    }

    [Theory]
    [InlineData("foo", "bar", false)]
    [InlineData("foobar", "br", false)]
    [InlineData("foobar", "bar", true)]
    [InlineData("Foobar", "BAR", true)]
    [InlineData("FooBaR", "Bar", true)]
    public void ends_with_ignore_case(string one, string suffix, bool expected)
    {
        one.EndsWithIgnoreCase(suffix).ShouldBe(expected);
    }
    
    [Theory]
    [InlineData("", 0)]
    [InlineData("foo=bar", 1)]
    [InlineData("foo=bar;", 1)]
    [InlineData("foo=bar;zap=42;", 2)]
    [InlineData("foo=bar;zap=42", 2)]
    [InlineData("foo=bar ;zap=42 ", 2)]
    public void removes_empty_entries_when_splitting_strings_into_array(string one, int expectedCount)
    {
        var array = one.ToDelimitedArray(';');

        array.Length.ShouldBe(expectedCount);
        array.All(x => x.IsNotEmpty()).ShouldBeTrue();
    }

    public struct NotAnEnum
    {
    }
}

public enum EnvTarget
{
    Machine,
    Process,
    User
}