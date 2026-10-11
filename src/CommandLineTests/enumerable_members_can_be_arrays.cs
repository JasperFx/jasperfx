using System.Linq.Expressions;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Internal.Conversion;
using JasperFx.CommandLine.Parsing;
using JasperFx.Core.Reflection;
using Shouldly;

namespace CommandLineTests;

// The reflection parser collects an enumerable flag or argument into a List<T>; assigning that to a
// T[] member threw, and the parser reported the throw as "Unknown argument or flag" for `--chapter A B`
public class enumerable_members_can_be_arrays
{
    private readonly ArrayInput theInput = new();

    private static Queue<string> tokens(params string[] values) => new(values);

    private static EnumerableFlag flagFor(Expression<Func<ArrayInput, object>> expression)
        => new(ReflectionHelper.GetProperty(expression), new Conversions());

    private static EnumerableArgument argumentFor(Expression<Func<ArrayInput, object>> expression)
        => new(ReflectionHelper.GetProperty(expression), new Conversions());

    [Fact]
    public void the_reflection_parser_sees_an_array_flag_as_an_enumerable_flag()
    {
        InputParser.GetHandlers(typeof(ArrayInput))
            .Single(x => x.MemberName == nameof(ArrayInput.ChapterFlag))
            .ShouldBeOfType<EnumerableFlag>();
    }

    [Fact]
    public void a_string_array_flag()
    {
        flagFor(x => x.ChapterFlag).Handle(theInput, tokens("--chapter", "Booking", "Volunteering")).ShouldBeTrue();

        theInput.ChapterFlag.ShouldBe(["Booking", "Volunteering"]);
    }

    [Fact]
    public void an_int_array_flag()
    {
        flagFor(x => x.AgesFlag).Handle(theInput, tokens("--ages", "3", "5")).ShouldBeTrue();

        theInput.AgesFlag.ShouldBe([3, 5]);
    }

    [Fact]
    public void an_array_flag_stops_at_the_next_flag()
    {
        var queue = tokens("--chapter", "Booking", "--ages", "3");

        flagFor(x => x.ChapterFlag).Handle(theInput, queue).ShouldBeTrue();

        theInput.ChapterFlag.ShouldBe(["Booking"]);
        queue.ShouldBe(["--ages", "3"]);
    }

    [Fact]
    public void a_list_flag_still_gets_a_list()
    {
        flagFor(x => x.TagsFlag).Handle(theInput, tokens("--tags", "a", "b")).ShouldBeTrue();

        theInput.TagsFlag.ShouldBeOfType<List<string>>().ShouldBe(["a", "b"]);
    }

    [Fact]
    public void a_string_array_argument()
    {
        argumentFor(x => x.Names).Handle(theInput, tokens("a", "b", "c")).ShouldBeTrue();

        theInput.Names.ShouldBe(["a", "b", "c"]);
    }

    public class ArrayInput
    {
        public string[] Names { get; set; } = [];

        [FlagAlias("chapter", 'c')]
        public string[] ChapterFlag { get; set; } = [];

        public int[] AgesFlag { get; set; } = [];

        public List<string> TagsFlag { get; set; } = [];
    }
}
