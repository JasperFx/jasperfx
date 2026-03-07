using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

public class EventTagQueryTests
{
    [Fact]
    public void empty_query_has_no_conditions()
    {
        var query = new EventTagQuery();
        query.Conditions.ShouldBeEmpty();
    }

    [Fact]
    public void or_with_event_type_and_tag()
    {
        var query = new EventTagQuery()
            .Or<AEvent, StudentId>(new StudentId("student-1"));

        query.Conditions.Count.ShouldBe(1);
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[0].TagType.ShouldBe(typeof(StudentId));
        query.Conditions[0].TagValue.ShouldBeOfType<StudentId>();
    }

    [Fact]
    public void or_with_tag_only()
    {
        var query = new EventTagQuery()
            .Or(new StudentId("student-1"));

        query.Conditions.Count.ShouldBe(1);
        query.Conditions[0].EventType.ShouldBeNull();
        query.Conditions[0].TagType.ShouldBe(typeof(StudentId));
    }

    [Fact]
    public void multiple_or_conditions()
    {
        var query = new EventTagQuery()
            .Or<AEvent, StudentId>(new StudentId("student-1"))
            .Or<BEvent, CourseId>(new CourseId("course-1"));

        query.Conditions.Count.ShouldBe(2);
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[0].TagType.ShouldBe(typeof(StudentId));
        query.Conditions[1].EventType.ShouldBe(typeof(BEvent));
        query.Conditions[1].TagType.ShouldBe(typeof(CourseId));
    }

    [Fact]
    public void tag_types_returns_distinct_types()
    {
        var query = new EventTagQuery()
            .Or<AEvent, StudentId>(new StudentId("student-1"))
            .Or<BEvent, StudentId>(new StudentId("student-2"))
            .Or<CEvent, CourseId>(new CourseId("course-1"));

        var tagTypes = query.TagTypes;
        tagTypes.Count.ShouldBe(2);
        tagTypes.ShouldContain(typeof(StudentId));
        tagTypes.ShouldContain(typeof(CourseId));
    }

    [Fact]
    public void fluent_api_returns_same_instance()
    {
        var query = new EventTagQuery();
        var result = query.Or<AEvent, StudentId>(new StudentId("student-1"));
        result.ShouldBeSameAs(query);
    }
}
