using System.Globalization;
using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Internal.Conversion;
using JasperFx.CommandLine.Parsing;
using JasperFx.CommandLine.Parsing.Generated;
using Shouldly;

namespace CommandLineTests;

public class InitCultureCommand : JasperFxCommand<InitCultureInput>
{
    public override bool Execute(InitCultureInput input) => true;
}

public class InitCultureInput
{
    public bool IsEnabledFlag { get; set; }
    public string InputFileFlag { get; set; } = string.Empty;
}

public class UsageCultureInput
{
    public string InputFileFlag { get; set; } = string.Empty;
    public List<string> IncludeFlag { get; set; } = new();
    public string Identifier { get; set; } = string.Empty;
    public List<string> Items { get; set; } = new();
}

public class CommandLineCultureTester
{
    // tr-TR lower-cases I to the dotless i
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
    public void command_names_do_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            CommandFactory.CommandNameFor(typeof(InitCultureCommand)).ShouldBe("initculture");
        });
    }

    [Fact]
    public void a_command_whose_name_has_an_upper_case_i_can_be_built_by_name()
    {
        inTurkish(() =>
        {
            var factory = new CommandFactory();
            factory.RegisterCommand<InitCultureCommand>();

            factory.Build("initculture").ShouldBeOfType<InitCultureCommand>();
            factory.Build("INITCULTURE").ShouldBeOfType<InitCultureCommand>();
        });
    }

    [Fact]
    public void flag_names_do_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            var aliases = InputParser.ToFlagAliases(typeof(InitCultureInput).GetProperty(nameof(InitCultureInput.IsEnabledFlag))!);

            aliases.LongForm.ShouldBe("--is-enabled");
            aliases.ShortForm.ShouldBe("-i");
            aliases.Matches("--is-enabled").ShouldBeTrue();
            aliases.Matches("--IS-ENABLED").ShouldBeTrue();
            aliases.Matches("--Is-Enabled").ShouldBeTrue();
        });
    }

    [Fact]
    public void the_flag_suffix_is_removed_in_any_culture()
    {
        inTurkish(() =>
        {
            InputParser.RemoveFlagSuffix("InputFILEFLAG").ShouldBe("InputFILE");
            InputParser.RemoveFlagSuffix("InputFileFlag").ShouldBe("InputFile");
        });
    }

    private static System.Reflection.PropertyInfo property(string name)
        => typeof(UsageCultureInput).GetProperty(name)!;

    // jasperfx#1248
    [Fact]
    public void usage_text_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            var conversions = new Conversions();

            new Flag(property(nameof(UsageCultureInput.InputFileFlag)), conversions).ToUsageDescription()
                .ShouldBe("[-i, --input-file <inputfile>]");
            new EnumerableFlag(property(nameof(UsageCultureInput.IncludeFlag)), conversions).ToUsageDescription()
                .ShouldBe("[-i, --include <include1 include2 include3 ...>]");
            new Argument(property(nameof(UsageCultureInput.Identifier)), conversions).ToUsageDescription()
                .ShouldBe("<identifier>");
            new EnumerableArgument(property(nameof(UsageCultureInput.Items)), conversions).ToUsageDescription()
                .ShouldBe("<items1 items2 items3 ...>");
        });
    }

    // jasperfx#1248
    [Fact]
    public void generated_usage_text_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            new GeneratedFlag<string>("InputFileFlag", "", new FlagAliases { LongForm = "--input-file", ShortForm = "-i" },
                    (_, _) => { }, x => x).ToUsageDescription()
                .ShouldBe("[-i, --input-file <inputfile>]");
            new GeneratedEnumerableFlag<string>("IncludeFlag", "", new FlagAliases { LongForm = "--include", ShortForm = "-i" },
                    (_, _) => { }, x => x).ToUsageDescription()
                .ShouldBe("[-i, --include <include1 include2 include3 ...>]");
            new GeneratedArgument<string>("Identifier", "", (_, _) => { }, x => x).ToUsageDescription()
                .ShouldBe("<identifier>");
            new GeneratedEnumerableArgument<string>("Items", "", (_, _) => { }, x => x).ToUsageDescription()
                .ShouldBe("<items1 items2 items3 ...>");
        });
    }

    // jasperfx#1248: "FRIDAY" lower-cased in tr-TR is "frıday", which missed "friday"
    [Fact]
    public void an_upper_case_day_of_the_week_is_recognized_in_any_culture()
    {
        inTurkish(() =>
        {
            var date = DateTimeConverter.GetDateTime("FRIDAY 14:30");

            date.DayOfWeek.ShouldBe(DayOfWeek.Friday);
            date.TimeOfDay.ShouldBe(new TimeSpan(14, 30, 0));
        });
    }
}
