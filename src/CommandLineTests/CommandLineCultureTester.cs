using System.Globalization;
using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Parsing;
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
}
