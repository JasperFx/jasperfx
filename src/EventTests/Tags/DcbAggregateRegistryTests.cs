using JasperFx.Events.Descriptors;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

// Coverage for jasperfx#546: runtime discovery of DCB aggregates used only inside a
// DCB operation. The registry is populated lazily on first DCB use, so registration
// must be idempotent, concurrency-safe, and surfaceable as descriptors by aggregate type.
public class DcbAggregateRegistryTests
{
    private static readonly ITagTypeRegistration StudentTag = TagTypeRegistration.Create<StudentId>();
    private static readonly ITagTypeRegistration CourseTag = TagTypeRegistration.Create<CourseId>();

    [Fact]
    public void register_is_idempotent_and_returns_the_first_registration()
    {
        var registry = new DcbAggregateRegistry();

        var first = registry.Register(typeof(Enrollment), [StudentTag, CourseTag]);
        var second = registry.Register(typeof(Enrollment), [StudentTag]); // second call, different tags

        // First registration wins; a lazily-instrumented hot path never double-registers.
        second.ShouldBeSameAs(first);
        registry.Registrations.Count.ShouldBe(1);
        first.TagTypes.Count.ShouldBe(2);
    }

    [Fact]
    public void try_find_locates_a_registered_aggregate_by_type()
    {
        var registry = new DcbAggregateRegistry();
        registry.Register(typeof(Enrollment), [StudentTag]);

        registry.TryFind(typeof(Enrollment), out var found).ShouldBeTrue();
        found!.AggregateType.ShouldBe(typeof(Enrollment));

        registry.TryFind(typeof(string), out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    public void registrations_snapshot_holds_every_discovered_aggregate()
    {
        var registry = new DcbAggregateRegistry();
        registry.Register(typeof(Enrollment), [StudentTag]);
        registry.Register(typeof(Attendance), [StudentTag, CourseTag]);

        registry.Registrations.Select(x => x.AggregateType)
            .ShouldBe([typeof(Enrollment), typeof(Attendance)], ignoreOrder: true);
    }

    [Fact]
    public void descriptor_mirrors_the_registration_type_and_tag_shape()
    {
        var registration = new DcbAggregateRegistration(typeof(Enrollment), [StudentTag, CourseTag]);

        var descriptor = DcbAggregateDescriptor.For(registration);

        descriptor.AggregateType.FullName.ShouldBe(typeof(Enrollment).FullName);
        descriptor.Tags.Count.ShouldBe(2);
        descriptor.Tags[0].TagType.FullName.ShouldBe(typeof(StudentId).FullName);
        descriptor.Tags[0].Name.ShouldBe(nameof(StudentId));
        descriptor.Tags[0].SimpleType.ShouldBe(typeof(string).FullName);
        descriptor.Tags[1].TagType.FullName.ShouldBe(typeof(CourseId).FullName);
    }

    [Fact]
    public void for_registry_snapshots_all_discovered_aggregates_as_descriptors()
    {
        var registry = new DcbAggregateRegistry();
        registry.Register(typeof(Enrollment), [StudentTag]);
        registry.Register(typeof(Attendance), [CourseTag]);

        var descriptors = DcbAggregateDescriptor.ForRegistry(registry);

        descriptors.Select(x => x.AggregateType.FullName)
            .ShouldBe([typeof(Enrollment).FullName, typeof(Attendance).FullName], ignoreOrder: true);
    }

    // DCB aggregates that would live buried in handler code — no registered projection name.
    private sealed class Enrollment;

    private sealed class Attendance;
}
