using JasperFx.CommandLine.Parsing;
using JasperFx.CommandLine.Parsing.Generated;
using Shouldly;

namespace CommandLineTests;

public class GeneratedFlagTester
{
    public enum Color
    {
        Red,
        Green,
        Blue
    }

    private static FlagAliases Aliases() => new() { LongForm = "--color", ShortForm = "-c" };

    [Fact]
    public void usage_description_for_non_nullable_enum_lists_values()
    {
        var flag = new GeneratedFlag<Color>("ColorFlag", "the color", Aliases(),
            (_, _) => { }, _ => Color.Red, isEnum: true);

        flag.ToUsageDescription().ShouldBe("[-c, --color Red|Green|Blue]");
    }

    // jasperfx#441: the source generator constructs GeneratedFlag<SomeEnum?> with isEnum: true
    // (IsEnum = isEnum || isNullableEnum). ToUsageDescription handed typeof(Nullable<SomeEnum>) to
    // Enum.GetNames and threw "Type provided must be an Enum", which crashed help / invalid-usage
    // rendering for every NetCoreInput-derived command (NetCoreInput.LogLevelFlag is LogLevel?).
    [Fact]
    public void usage_description_for_nullable_enum_resolves_underlying_type()
    {
        var flag = new GeneratedFlag<Color?>("ColorFlag", "the color", Aliases(),
            (_, _) => { }, _ => Color.Red, isEnum: true);

        Should.NotThrow(() => flag.ToUsageDescription())
            .ShouldBe("[-c, --color Red|Green|Blue]");
    }
}
