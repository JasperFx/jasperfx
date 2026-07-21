using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

// Coverage for jasperfx#545: a serializable EventTagQuery that survives a message hop
// and resolves back to CLR types against the registered tag/event graph, carrying the
// full rich query (OR conditions + AndEventsOfType event-type filters) rather than the
// lossy AND-only Dictionary<string,string> form.
public class EventTagQuerySpecTests
{
    // The "registered graph" the service side resolves against.
    private static readonly Func<JasperFx.Descriptors.TypeDescriptor, Type?> Resolver =
        EventTagQuerySpec.ResolverFor([
            typeof(StudentId), typeof(CourseId),
            typeof(AEvent), typeof(BEvent), typeof(CEvent)
        ]);

    [Fact]
    public void round_trips_or_conditions_with_event_type_filters()
    {
        var original = new EventTagQuery()
            .Or<AEvent, StudentId>(new StudentId("student-1"))
            .Or<BEvent, CourseId>(new CourseId("course-1"));

        var spec = EventTagQuerySpec.From(original);
        var rehydrated = spec.Resolve(Resolver);

        rehydrated.Conditions.Count.ShouldBe(2);

        rehydrated.Conditions[0].EventType.ShouldBe(typeof(AEvent));
        rehydrated.Conditions[0].TagType.ShouldBe(typeof(StudentId));
        rehydrated.Conditions[0].TagValue.ShouldBe(new StudentId("student-1"));

        rehydrated.Conditions[1].EventType.ShouldBe(typeof(BEvent));
        rehydrated.Conditions[1].TagType.ShouldBe(typeof(CourseId));
        rehydrated.Conditions[1].TagValue.ShouldBe(new CourseId("course-1"));
    }

    [Fact]
    public void round_trips_tag_only_condition_with_null_event_type()
    {
        var original = new EventTagQuery()
            .Or(new StudentId("student-9"));

        var rehydrated = EventTagQuerySpec.From(original).Resolve(Resolver);

        rehydrated.Conditions.Count.ShouldBe(1);
        rehydrated.Conditions[0].EventType.ShouldBeNull();
        rehydrated.Conditions[0].TagType.ShouldBe(typeof(StudentId));
        rehydrated.Conditions[0].TagValue.ShouldBe(new StudentId("student-9"));
    }

    [Fact]
    public void round_trips_and_events_of_type_fan_out()
    {
        // For(tag).AndEventsOfType<A,B,C>() expands to one condition per event type,
        // all sharing the tag value — the expressiveness the dictionary form can't carry.
        var original = EventTagQuery
            .For(new StudentId("student-42"))
            .AndEventsOfType<AEvent, BEvent, CEvent>();

        var rehydrated = EventTagQuerySpec.From(original).Resolve(Resolver);

        rehydrated.Conditions.Count.ShouldBe(3);
        rehydrated.Conditions.Select(x => x.EventType)
            .ShouldBe([typeof(AEvent), typeof(BEvent), typeof(CEvent)]);
        rehydrated.Conditions.ShouldAllBe(x =>
            x.TagType == typeof(StudentId) && x.TagValue.Equals(new StudentId("student-42")));
    }

    [Fact]
    public void unresolvable_type_raises_a_precise_error()
    {
        var spec = EventTagQuerySpec.From(new EventTagQuery().Or(new StudentId("x")));

        // A resolver that knows nothing → the tag type can't be resolved.
        var ex = Should.Throw<UnknownTagQueryTypeException>(() =>
            spec.Resolve(EventTagQuerySpec.ResolverFor([])));

        ex.Descriptor.FullName.ShouldBe(typeof(StudentId).FullName);
    }
}
