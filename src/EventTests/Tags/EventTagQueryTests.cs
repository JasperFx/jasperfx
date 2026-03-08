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
    public void or_with_tag_only_sets_context_without_adding_condition()
    {
        var query = new EventTagQuery()
            .Or(new StudentId("student-1"));

        query.Conditions.ShouldBeEmpty();
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

    [Fact]
    public void for_static_factory_creates_query_with_tag_context()
    {
        var studentId = new StudentId("student-1");
        var query = EventTagQuery
            .For(studentId)
            .AndEventsOfType<AEvent>();

        query.Conditions.Count.ShouldBe(1);
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[0].TagType.ShouldBe(typeof(StudentId));
        query.Conditions[0].TagValue.ShouldBe(studentId);
    }

    [Fact]
    public void for_with_multiple_event_types()
    {
        var courseId = new CourseId("course-1");
        var query = EventTagQuery
            .For(courseId)
            .AndEventsOfType<AEvent, BEvent, CEvent>();

        query.Conditions.Count.ShouldBe(3);
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[0].TagType.ShouldBe(typeof(CourseId));
        query.Conditions[0].TagValue.ShouldBe(courseId);
        query.Conditions[1].EventType.ShouldBe(typeof(BEvent));
        query.Conditions[2].EventType.ShouldBe(typeof(CEvent));
    }

    [Fact]
    public void for_and_or_chaining()
    {
        var courseId = new CourseId("course-1");
        var studentId = new StudentId("student-1");

        var query = EventTagQuery
            .For(courseId)
            .AndEventsOfType<AEvent, BEvent>()
            .Or(studentId)
            .AndEventsOfType<CEvent>();

        query.Conditions.Count.ShouldBe(3);

        // CourseId conditions
        query.Conditions[0].TagType.ShouldBe(typeof(CourseId));
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[1].TagType.ShouldBe(typeof(CourseId));
        query.Conditions[1].EventType.ShouldBe(typeof(BEvent));

        // StudentId condition
        query.Conditions[2].TagType.ShouldBe(typeof(StudentId));
        query.Conditions[2].EventType.ShouldBe(typeof(CEvent));
    }

    [Fact]
    public void shorthand_produces_same_result_as_verbose()
    {
        var courseId = new CourseId("course-1");
        var studentId = new StudentId("student-1");

        // Verbose syntax
        var verbose = new EventTagQuery()
            .Or<AEvent, CourseId>(courseId)
            .Or<BEvent, CourseId>(courseId)
            .Or<CEvent, StudentId>(studentId);

        // Shorthand syntax
        var shorthand = EventTagQuery
            .For(courseId)
            .AndEventsOfType<AEvent, BEvent>()
            .Or(studentId)
            .AndEventsOfType<CEvent>();

        shorthand.Conditions.Count.ShouldBe(verbose.Conditions.Count);
        for (int i = 0; i < verbose.Conditions.Count; i++)
        {
            shorthand.Conditions[i].EventType.ShouldBe(verbose.Conditions[i].EventType);
            shorthand.Conditions[i].TagType.ShouldBe(verbose.Conditions[i].TagType);
            shorthand.Conditions[i].TagValue.ShouldBe(verbose.Conditions[i].TagValue);
        }
    }

    [Fact]
    public void and_events_of_type_with_four_types()
    {
        var courseId = new CourseId("course-1");
        var query = EventTagQuery
            .For(courseId)
            .AndEventsOfType<AEvent, BEvent, CEvent, DEvent>();

        query.Conditions.Count.ShouldBe(4);
        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[1].EventType.ShouldBe(typeof(BEvent));
        query.Conditions[2].EventType.ShouldBe(typeof(CEvent));
        query.Conditions[3].EventType.ShouldBe(typeof(DEvent));
    }

    [Fact]
    public void and_events_of_type_throws_without_tag_context()
    {
        var query = new EventTagQuery();
        Should.Throw<InvalidOperationException>(() => query.AndEventsOfType<AEvent>());
    }

    [Fact]
    public void or_sets_new_tag_context_for_and_events_of_type()
    {
        var courseId = new CourseId("course-1");
        var studentId = new StudentId("student-1");

        // Or() just sets tag context, AndEventsOfType adds the conditions
        var query = new EventTagQuery()
            .Or(courseId)
            .AndEventsOfType<AEvent>()
            .Or(studentId)
            .AndEventsOfType<BEvent>();

        query.Conditions.Count.ShouldBe(2);

        query.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        query.Conditions[0].TagType.ShouldBe(typeof(CourseId));

        // Or(studentId) switched context, so AndEventsOfType uses StudentId
        query.Conditions[1].EventType.ShouldBe(typeof(BEvent));
        query.Conditions[1].TagType.ShouldBe(typeof(StudentId));
    }
}
