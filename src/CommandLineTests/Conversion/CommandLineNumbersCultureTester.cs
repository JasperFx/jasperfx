using System.Globalization;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Internal.Conversion;
using JasperFx.CommandLine.Parsing;
using Shouldly;

namespace CommandLineTests.Conversion;

// Numbers on the command line are read with the invariant culture, so a value means
// the same thing on every machine.
public class CommandLineNumbersCultureTester
{
    private static void inCulture(string culture, Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static NumbersInput parse(List<ITokenHandler> handlers, params string[] args)
    {
        var input = new NumbersInput();
        var tokens = new Queue<string>(ArgPreprocessor.Process(args));
        while (tokens.Count > 0)
        {
            if (!handlers.Any(x => x.Handle(input, tokens)))
            {
                throw new InvalidOperationException($"Nothing handled '{tokens.Peek()}'");
            }
        }

        return input;
    }

    [Fact]
    public void decimal_numbers_do_not_depend_on_the_current_culture()
    {
        var conversions = new Conversions();

        // de-DE reads "." as a group separator, so "1.5" used to come back as 15
        inCulture("de-DE", () =>
        {
            conversions.Convert(typeof(double), "1.5").ShouldBe(1.5);
            conversions.Convert(typeof(float), "1.5").ShouldBe(1.5f);
            conversions.Convert(typeof(decimal), "12.34").ShouldBe(12.34m);
        });
    }

    [Fact]
    public void negative_whole_numbers_do_not_depend_on_the_current_culture()
    {
        var conversions = new Conversions();

        // ar-SA writes the negative sign with a direction mark, so "-5" used to throw
        inCulture("ar-SA", () =>
        {
            conversions.Convert(typeof(int), "-5").ShouldBe(-5);
            conversions.Convert(typeof(long), "-5").ShouldBe(-5L);
            conversions.Convert(typeof(short), "-5").ShouldBe((short)-5);
            conversions.Convert(typeof(sbyte), "-5").ShouldBe((sbyte)-5);
        });
    }

    [Fact]
    public void today_offsets_do_not_depend_on_the_current_culture()
    {
        inCulture("ar-SA", () =>
        {
            DateTimeConverter.GetDateTime("TODAY-3").ShouldBe(DateTime.Today.AddDays(-3));
        });
    }

    [Fact]
    public void a_negative_decimal_argument_is_not_split_into_flags()
    {
        // fr-FR's decimal separator is a comma, so "-1.5" used to be read as the flags -1 -. -5
        inCulture("fr-FR", () =>
        {
            ArgPreprocessor.Process(["-1.5"]).ShouldBe(["-1.5"]);
        });
    }

    [Fact]
    public void reflection_parser_reads_numbers_the_same_in_every_culture()
    {
        inCulture("fr-FR", () =>
        {
            var input = parse(InputParser.GetHandlers(typeof(NumbersInput)), "-1.5", "--amount", "12.34", "--count", "3");
            input.Ratio.ShouldBe(-1.5);
            input.AmountFlag.ShouldBe(12.34m);
            input.CountFlag.ShouldBe(3);
        });
    }

    [Fact]
    public void generated_parser_reads_numbers_the_same_in_every_culture()
    {
        // An argument handler latches after its first value, so each parse gets fresh handlers
        List<ITokenHandler> generated()
        {
            var handlers = GeneratedParserRegistry.TryGetHandlers(typeof(NumbersInput));
            handlers.ShouldNotBeNull("The InputParserGenerator should have produced a parser for NumbersInput");
            return handlers;
        }

        inCulture("fr-FR", () =>
        {
            var input = parse(generated(), "-1.5", "--amount", "12.34", "--count", "3");
            input.Ratio.ShouldBe(-1.5);
            input.AmountFlag.ShouldBe(12.34m);
            input.CountFlag.ShouldBe(3);
        });

        inCulture("de-DE", () =>
        {
            parse(generated(), "1.5").Ratio.ShouldBe(1.5);
        });
    }
}

public class NumbersInput
{
    public double Ratio { get; set; }
    public decimal AmountFlag { get; set; }
    public int CountFlag { get; set; }
}

public class NumbersCommand : JasperFxCommand<NumbersInput>
{
    public override bool Execute(NumbersInput input)
    {
        return true;
    }
}
