using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

// Coverage for jasperfx#801's shared tag-name matcher: the one place that decides what a key in
// EventQuery.TagValues (or the dictionary QueryByTagsAsync overload) refers to. It lives in the
// abstraction rather than in each store because a remote caller cannot discover which spelling a
// given engine accepts.
public class TagTypeRegistrationExtensionsTests
{
    private static readonly ITagTypeRegistration[] _registered =
    [
        TagTypeRegistration.Create<StudentId>("student"),
        TagTypeRegistration.Create<CourseId>("course")
    ];

    [Fact]
    public void matches_the_clr_simple_name()
    {
        _registered.FindByTagName("StudentId")!.TagType.ShouldBe(typeof(StudentId));
    }

    [Fact]
    public void matches_the_registered_table_suffix()
    {
        _registered.FindByTagName("course")!.TagType.ShouldBe(typeof(CourseId));
    }

    [Fact]
    public void matching_is_case_insensitive_in_both_spellings()
    {
        _registered.FindByTagName("studentid")!.TagType.ShouldBe(typeof(StudentId));
        _registered.FindByTagName("COURSE")!.TagType.ShouldBe(typeof(CourseId));
    }

    [Fact]
    public void an_unknown_name_finds_nothing()
    {
        _registered.FindByTagName("no_such_tag").ShouldBeNull();
    }

    /// <summary>
    /// A CLR name is tried across every registration before any suffix is considered, so a tag type
    /// whose suffix happens to spell another type's name cannot shadow it.
    /// </summary>
    [Fact]
    public void the_clr_name_wins_over_another_types_suffix()
    {
        ITagTypeRegistration[] colliding =
        [
            // CourseId's suffix is spelled exactly like StudentId's CLR name.
            TagTypeRegistration.Create<CourseId>("StudentId"),
            TagTypeRegistration.Create<StudentId>("student")
        ];

        colliding.FindByTagName("StudentId")!.TagType.ShouldBe(typeof(StudentId));
        colliding.FindByTagName("student")!.TagType.ShouldBe(typeof(StudentId));
        colliding.FindByTagName("CourseId")!.TagType.ShouldBe(typeof(CourseId));
    }

    [Fact]
    public void require_returns_the_registration_when_the_name_is_known()
    {
        _registered.RequireByTagName("student").TagType.ShouldBe(typeof(StudentId));
    }

    [Fact]
    public void require_refuses_an_unknown_name_and_lists_what_is_registered()
    {
        // Loud rather than empty: "that tag type does not exist here" and "no event carries that
        // tag" must not read alike to a caller filtering events.
        var ex = Should.Throw<ArgumentException>(() => _registered.RequireByTagName("no_such_tag", "tags"));

        ex.ParamName.ShouldBe("tags");
        ex.Message.ShouldContain("no_such_tag");
        ex.Message.ShouldContain("StudentId");
        ex.Message.ShouldContain("course");
    }

    [Fact]
    public void an_empty_graph_finds_nothing_and_refuses_everything()
    {
        Array.Empty<ITagTypeRegistration>().FindByTagName("StudentId").ShouldBeNull();
        Should.Throw<ArgumentException>(() => Array.Empty<ITagTypeRegistration>().RequireByTagName("StudentId"));
    }
}
