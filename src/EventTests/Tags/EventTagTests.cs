using JasperFx.Events;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

// Strong-typed identifier types for testing
public record StudentId(string Value);
public record CourseId(string Value);
public record InvoiceId(Guid Value);

public class EventTagTests
{
    [Fact]
    public void add_single_tag_to_event()
    {
        var studentId = new StudentId("student-1");
        var e = Event.For(new AEvent());
        e.AddTag(studentId);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(1);
        e.Tags[0].TagType.ShouldBe(typeof(StudentId));
        e.Tags[0].Value.ShouldBe(studentId);
    }

    [Fact]
    public void add_multiple_tags_of_different_types()
    {
        var studentId = new StudentId("student-1");
        var courseId = new CourseId("course-1");
        var e = Event.For(new AEvent());
        e.AddTag(studentId);
        e.AddTag(courseId);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(2);
        e.Tags[0].TagType.ShouldBe(typeof(StudentId));
        e.Tags[0].Value.ShouldBe(studentId);
        e.Tags[1].TagType.ShouldBe(typeof(CourseId));
        e.Tags[1].Value.ShouldBe(courseId);
    }

    [Fact]
    public void add_multiple_tags_of_same_type()
    {
        var student1 = new StudentId("student-1");
        var student2 = new StudentId("student-2");
        var e = Event.For(new AEvent());
        e.AddTag(student1);
        e.AddTag(student2);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(2);
        e.Tags[0].Value.ShouldBe(student1);
        e.Tags[1].Value.ShouldBe(student2);
    }

    [Fact]
    public void tags_are_null_when_none_added()
    {
        var e = Event.For(new AEvent());
        e.Tags.ShouldBeNull();
    }

    [Fact]
    public void with_tag_fluent_api()
    {
        var studentId = new StudentId("student-1");
        var e = Event.For(new AEvent()).WithTag(studentId);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(1);
        e.Tags[0].TagType.ShouldBe(typeof(StudentId));
        e.Tags[0].Value.ShouldBe(studentId);
    }

    [Fact]
    public void with_tag_multiple_params()
    {
        var studentId = new StudentId("student-1");
        var courseId = new CourseId("course-1");
        var e = Event.For(new AEvent()).WithTag(studentId, courseId);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(2);
        e.Tags[0].TagType.ShouldBe(typeof(StudentId));
        e.Tags[1].TagType.ShouldBe(typeof(CourseId));
    }

    [Fact]
    public void with_tag_guid_based_id()
    {
        var invoiceId = new InvoiceId(Guid.NewGuid());
        var e = Event.For(new AEvent()).WithTag(invoiceId);

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(1);
        e.Tags[0].TagType.ShouldBe(typeof(InvoiceId));
        e.Tags[0].Value.ShouldBe(invoiceId);
    }

    [Fact]
    public void add_raw_event_tag()
    {
        var e = Event.For(new AEvent());
        e.AddTag(new EventTag(typeof(StudentId), "raw-value"));

        e.Tags.ShouldNotBeNull();
        e.Tags.Count.ShouldBe(1);
        e.Tags[0].TagType.ShouldBe(typeof(StudentId));
        e.Tags[0].Value.ShouldBe("raw-value");
    }
}
