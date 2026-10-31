using System.Globalization;
using JasperFx.Events;
using JasperFx.Events.Tags;
using NSubstitute;
using Shouldly;

namespace EventTests.Tags;

public record WardId(Guid Value);

public record CohortName(string Value);

public record BedNumber(int Value);

public record VisitorId(Guid Value);

public record Admitted(string Patient);

public record AdmittedToWard(WardId Ward, string Patient);

public class WardOccupancy;

public class OtherAggregate;

public class EventBoundaryTests
{
    private const string TheTenant = "tenant-b";

    private static readonly ITagTypeRegistration Ward =
        TagTypeRegistration.Create<WardId>("ward").ForAggregate<WardOccupancy>();

    private static readonly ITagTypeRegistration Cohort =
        TagTypeRegistration.Create<CohortName>("cohort").ForAggregate<WardOccupancy>();

    private static readonly ITagTypeRegistration Bed =
        TagTypeRegistration.Create<BedNumber>("bed").ForAggregate<WardOccupancy>();

    // Registered with no aggregate, so it can be queried on but routes nothing
    private static readonly ITagTypeRegistration Visitor = TagTypeRegistration.Create<VisitorId>("visitor");

    private static readonly ITagTypeRegistration[] AllTagTypes = [Ward, Cohort, Bed, Visitor];

    private readonly TestRegistry theRegistry = new();
    private readonly StoreLikeOperations theOperations = new(TheTenant);

    private EventBoundary<WardOccupancy> BoundaryOver(StoreLikeOperations operations,
        WardOccupancy? aggregate = null, params IEvent[] loaded)
        => new(operations.Operations, theRegistry, AllTagTypes, aggregate, loaded);

    private EventBoundary<WardOccupancy> theBoundary() => BoundaryOver(theOperations);

    private IEvent Tagged(params object[] tags) => theRegistry.BuildEvent(new Admitted("Frodo")).WithTag(tags);

    // ---- what a fetch reads ----

    [Fact]
    public void exposes_what_the_store_read()
    {
        var aggregate = new WardOccupancy();
        var first = new Event<Admitted>(new Admitted("Frodo")) { Sequence = 3 };
        var second = new Event<Admitted>(new Admitted("Sam")) { Sequence = 11 };

        var boundary = BoundaryOver(theOperations, aggregate, first, second);

        boundary.Aggregate.ShouldBeSameAs(aggregate);
        boundary.Events.ShouldBe([first, second]);
        boundary.LastSeenSequence.ShouldBe(11);
    }

    [Fact]
    public void a_boundary_over_nothing_has_seen_sequence_zero()
    {
        var boundary = theBoundary();

        boundary.Aggregate.ShouldBeNull();
        boundary.Events.ShouldBeEmpty();
        boundary.LastSeenSequence.ShouldBe(0);
    }

    // ---- routing by tag ----

    [Fact]
    public void routes_to_the_stream_the_first_aggregate_tag_names()
    {
        var ward = Guid.NewGuid();

        theBoundary().AppendOne(Tagged(new WardId(ward)));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.Id.ShouldBe(ward);
        stream.Key.ShouldBeNull();
        stream.ActionType.ShouldBe(StreamActionType.Append);
    }

    [Fact]
    public void a_tag_without_an_aggregate_is_passed_over_for_one_with()
    {
        var ward = Guid.NewGuid();

        theBoundary().AppendOne(Tagged(new VisitorId(Guid.NewGuid()), new WardId(ward)));

        theOperations.Streams.ShouldHaveSingleItem().Id.ShouldBe(ward);
    }

    [Fact]
    public void routes_by_string_key_under_string_identity()
    {
        theRegistry.StreamIdentity = StreamIdentity.AsString;

        theBoundary().AppendOne(Tagged(new CohortName("spring-intake")));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.Key.ShouldBe("spring-intake");
        stream.Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void two_appends_for_one_tag_value_land_in_one_stream()
    {
        var ward = Guid.NewGuid();
        var boundary = theBoundary();

        boundary.AppendOne(Tagged(new WardId(ward)));
        boundary.AppendOne(Tagged(new WardId(ward)));

        theOperations.Streams.ShouldHaveSingleItem().Events.Count.ShouldBe(2);
    }

    /// <summary>
    /// Marten and Polecat stamped this on a stream they created; Fisher's boundary did not, so its
    /// tag-derived streams were written with no type.
    /// </summary>
    [Fact]
    public void a_new_stream_takes_the_aggregate_type_of_the_tag_that_named_it()
    {
        theBoundary().AppendOne(Tagged(new WardId(Guid.NewGuid())));

        theOperations.Streams.ShouldHaveSingleItem().AggregateType.ShouldBe(typeof(WardOccupancy));
    }

    [Fact]
    public void a_stream_already_pending_as_another_aggregate_keeps_its_type()
    {
        var ward = Guid.NewGuid();
        theOperations.Operations.Append(ward, new Admitted("Merry")).AggregateType = typeof(OtherAggregate);

        theBoundary().AppendOne(Tagged(new WardId(ward)));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.AggregateType.ShouldBe(typeof(OtherAggregate));
        stream.Events.Count.ShouldBe(2);
    }

    // ---- a tag value that does not fit the store's stream identity ----

    /// <summary>
    /// <see cref="ITagTypeRegistration.ForAggregate{T}"/> does not stop a string tag naming an aggregate on
    /// a Guid store, and Marten's own tests register one. Marten and Polecat built a keyed
    /// <see cref="StreamAction"/> for it with an empty Guid; Fisher threw InvalidCastException.
    /// </summary>
    [Fact]
    public void a_string_tag_cannot_name_a_guid_stream()
    {
        var ward = Guid.NewGuid();

        theBoundary().AppendOne(Tagged(new CohortName("spring-intake"), new WardId(ward)));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.Id.ShouldBe(ward);
        stream.Key.ShouldBeNull();
    }

    [Fact]
    public void a_numeric_tag_cannot_name_a_guid_stream()
    {
        var ward = Guid.NewGuid();

        theBoundary().AppendOne(Tagged(new BedNumber(7), new WardId(ward)));

        theOperations.Streams.ShouldHaveSingleItem().Id.ShouldBe(ward);
    }

    [Fact]
    public void when_no_tag_fits_a_guid_store_the_event_gets_its_own_stream()
    {
        theBoundary().AppendOne(Tagged(new CohortName("spring-intake")));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.ActionType.ShouldBe(StreamActionType.Start);
        stream.Id.ShouldNotBe(Guid.Empty);
        stream.Key.ShouldBeNull();
    }

    [Fact]
    public void a_guid_tag_names_a_string_stream_in_canonical_form()
    {
        theRegistry.StreamIdentity = StreamIdentity.AsString;
        var ward = Guid.Parse("0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9");

        theBoundary().AppendOne(Tagged(new WardId(ward)));

        theOperations.Streams.ShouldHaveSingleItem().Key.ShouldBe("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");
    }

    /// <summary>
    /// Every process has to derive the same stream from one tag value, whatever its culture. Fisher's
    /// boundary used plain ToString() here.
    /// </summary>
    [Fact]
    public void a_numeric_tag_names_the_same_string_stream_under_any_culture()
    {
        theRegistry.StreamIdentity = StreamIdentity.AsString;

        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;

        try
        {
            theBoundary().AppendOne(Tagged(new BedNumber(-7)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        theOperations.Streams.ShouldHaveSingleItem().Key.ShouldBe("-7");
    }

    // ---- nothing routes it ----

    [Fact]
    public void an_event_no_tag_routes_starts_a_stream_of_its_own()
    {
        var boundary = theBoundary();

        boundary.AppendOne(Tagged(new VisitorId(Guid.NewGuid())));
        boundary.AppendOne(Tagged(new VisitorId(Guid.NewGuid())));

        theOperations.Streams.Count.ShouldBe(2);
        theOperations.Streams.Select(x => x.Id).Distinct().Count().ShouldBe(2);

        foreach (var stream in theOperations.Streams)
        {
            // Start rather than Append: nothing can already hold an id generated just now, so there is
            // no existing stream for the store to go and read a version from.
            stream.ActionType.ShouldBe(StreamActionType.Start);
            stream.AggregateType.ShouldBeNull();
            stream.Id.ShouldNotBe(Guid.Empty);
        }
    }

    [Fact]
    public void under_string_identity_an_unrouted_event_starts_a_keyed_stream()
    {
        theRegistry.StreamIdentity = StreamIdentity.AsString;

        theBoundary().AppendOne(Tagged(new VisitorId(Guid.NewGuid())));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.ActionType.ShouldBe(StreamActionType.Start);
        stream.Key.ShouldNotBeNullOrEmpty();
        stream.Id.ShouldBe(Guid.Empty);
    }

    // ---- where the tags come from ----

    [Fact]
    public void tags_are_inferred_from_the_event_when_it_has_none()
    {
        var ward = Guid.NewGuid();

        theBoundary().AppendOne(new AdmittedToWard(new WardId(ward), "Frodo"));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.Id.ShouldBe(ward);

        var tag = stream.Events.ShouldHaveSingleItem().Tags.ShouldHaveSingleItem();
        tag.TagType.ShouldBe(typeof(WardId));
        tag.Value.ShouldBe(new WardId(ward));
    }

    [Fact]
    public void an_event_with_no_tags_and_nothing_to_infer_is_refused()
    {
        var ex = Should.Throw<InvalidOperationException>(() => theBoundary().AppendOne(new Admitted("Frodo")));

        ex.Message.ShouldContain(nameof(Admitted));
        theOperations.Streams.ShouldBeEmpty();
    }

    /// <summary>
    /// Marten's <c>TagWith</c> rules run in <c>BuildEvent</c>, including on an event the caller wrapped, so
    /// the boundary has to build before it reads the tags. Fisher's skipped BuildEvent for a wrapped event.
    /// </summary>
    [Fact]
    public void tags_a_store_adds_while_building_the_event_route_it()
    {
        var ward = Guid.NewGuid();
        theRegistry.DerivedTag = _ => new WardId(ward);

        theBoundary().AppendOne(new Event<Admitted>(new Admitted("Frodo")));

        theOperations.Streams.ShouldHaveSingleItem().Id.ShouldBe(ward);
    }

    [Fact]
    public void an_already_wrapped_event_is_not_wrapped_again()
    {
        var wrapped = Tagged(new WardId(Guid.NewGuid()));
        wrapped.SetHeader("source", "triage");

        theBoundary().AppendOne(wrapped);

        theOperations.Streams.ShouldHaveSingleItem().Events.ShouldHaveSingleItem().ShouldBeSameAs(wrapped);
    }

    [Fact]
    public void append_many_appends_in_order_from_either_overload()
    {
        var ward = new WardId(Guid.NewGuid());
        var one = Tagged(ward);
        var two = Tagged(ward);
        var three = Tagged(ward);

        var boundary = theBoundary();
        boundary.AppendMany(one, two);
        boundary.AppendMany(new List<object> { three });

        theOperations.Streams.ShouldHaveSingleItem().Events.ShouldBe([one, two, three]);
    }

    [Fact]
    public void null_is_refused()
    {
        var boundary = theBoundary();

        Should.Throw<ArgumentNullException>(() => boundary.AppendOne(null!));
        Should.Throw<ArgumentNullException>(() => boundary.AppendMany((IEnumerable<object>)null!));
    }

    // ---- tenancy ----

    /// <summary>
    /// The drift between Marten and Polecat, and neither was right. A session and its <c>ForTenant</c>
    /// scope share one unit of work and differ in tenant, so this is the arrangement that tells them
    /// apart. Marten added the stream with no tenant for the parent's save to fill in, which puts the
    /// events in the parent's tenant, after reading the stream state with no tenant filter. Polecat
    /// stamped <c>_session.TenantId</c>, which is the parent again because Polecat's scope builds its
    /// operations over the parent session.
    /// </summary>
    [Fact]
    public void the_tenant_is_the_one_the_boundary_was_fetched_through_and_not_the_sessions()
    {
        var unitOfWork = new List<StreamAction>();
        var scope = new StoreLikeOperations("scope-tenant", unitOfWork);
        var parent = new StoreLikeOperations("parent-tenant", unitOfWork);

        // The parent has work of its own in the same unit of work
        var theirs = parent.Operations.Append(Guid.NewGuid(), new Admitted("Merry"));

        BoundaryOver(scope).AppendOne(Tagged(new WardId(Guid.NewGuid())));

        var mine = unitOfWork.Single(x => x != theirs);
        mine.TenantId.ShouldBe("scope-tenant");
        mine.Events.ShouldAllBe(x => x.TenantId == "scope-tenant");

        theirs.TenantId.ShouldBe("parent-tenant");
    }

    [Fact]
    public void a_stream_of_its_own_is_in_that_tenant_too()
    {
        theBoundary().AppendOne(Tagged(new VisitorId(Guid.NewGuid())));

        var stream = theOperations.Streams.ShouldHaveSingleItem();
        stream.TenantId.ShouldBe(TheTenant);
        stream.Events.ShouldHaveSingleItem().TenantId.ShouldBe(TheTenant);
    }

    // ---- concurrency stays with the store ----

    /// <summary>
    /// How a boundary is enforced is per store: tag-version tables in Marten and Polecat, a re-query under
    /// the write lock in Fisher. A shared boundary that asserted anything of its own would be wrong on at
    /// least one of them.
    /// </summary>
    [Fact]
    public void the_appends_assert_nothing_about_the_stream()
    {
        theBoundary().AppendOne(Tagged(new WardId(Guid.NewGuid())));
        theBoundary().AppendOne(Tagged(new VisitorId(Guid.NewGuid())));

        foreach (var stream in theOperations.Streams)
        {
            stream.ExpectedVersionOnServer.ShouldBeNull();
            stream.AlwaysEnforceConsistency.ShouldBeFalse();
        }
    }

    [Fact]
    public void writing_only_ever_uses_the_plain_append_and_start()
    {
        var boundary = theBoundary();
        boundary.AppendOne(Tagged(new WardId(Guid.NewGuid())));
        boundary.AppendOne(Tagged(new VisitorId(Guid.NewGuid())));

        var calls = theOperations.Operations.ReceivedCalls().ToArray();

        calls.Select(x => x.GetMethodInfo().Name).ShouldBe([nameof(IEventOperations.Append), nameof(IEventOperations.StartStream)]);

        // No expected-version overload, which is a three argument call
        calls.ShouldAllBe(x => x.GetArguments().Length == 2);
    }

    /// <summary>
    /// A registry shaped like every store's, which <see cref="EventRegistry"/> itself is not: an event
    /// already built passes through, and building is where a store derives what else it carries.
    /// </summary>
    private class TestRegistry : EventRegistry
    {
        public Func<object, object>? DerivedTag { get; set; }

        public override IEvent BuildEvent(object eventData)
        {
            var built = eventData as IEvent ?? base.BuildEvent(eventData);

            if (DerivedTag != null && built.Tags is not { Count: > 0 })
            {
                built.WithTag(DerivedTag(built.Data));
            }

            return built;
        }
    }

    /// <summary>
    /// Stands in for a store's event operations, behaving as <c>Append</c> and <c>StartStream</c> do on all
    /// three stores: an append merges into a stream already pending, and a new action takes the tenant of
    /// these operations. Two instances can share one unit of work, as a session and its <c>ForTenant</c>
    /// scope do.
    /// </summary>
    private class StoreLikeOperations
    {
        private readonly string _tenantId;

        public StoreLikeOperations(string tenantId, List<StreamAction>? unitOfWork = null)
        {
            _tenantId = tenantId;
            Streams = unitOfWork ?? [];

            Operations.Append(Arg.Any<Guid>(), Arg.Any<object[]>()).Returns(call =>
                Append(call.ArgAt<Guid>(0), x => x.Id == call.ArgAt<Guid>(0), call.ArgAt<object[]>(1)));

            Operations.Append(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
                Append(call.ArgAt<string>(0), x => x.Key == call.ArgAt<string>(0), call.ArgAt<object[]>(1)));

            Operations.StartStream(Arg.Any<Guid>(), Arg.Any<object[]>()).Returns(call =>
                Track(new StreamAction(call.ArgAt<Guid>(0), StreamActionType.Start), call.ArgAt<object[]>(1)));

            Operations.StartStream(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
                Track(new StreamAction(call.ArgAt<string>(0), StreamActionType.Start), call.ArgAt<object[]>(1)));
        }

        public IEventOperations Operations { get; } = Substitute.For<IEventOperations>();

        public List<StreamAction> Streams { get; }

        private StreamAction Append(object id, Func<StreamAction, bool> sameStream, object[] events)
        {
            var existing = Streams.FirstOrDefault(sameStream);

            if (existing != null)
            {
                existing.AddEvents(Wrapped(events));
                return existing;
            }

            var stream = id is Guid guid
                ? new StreamAction(guid, StreamActionType.Append)
                : new StreamAction((string)id, StreamActionType.Append);

            return Track(stream, events);
        }

        private StreamAction Track(StreamAction stream, object[] events)
        {
            stream.AddEvents(Wrapped(events));
            stream.TenantId = _tenantId;
            Streams.Add(stream);

            return stream;
        }

        private static IEvent[] Wrapped(object[] events)
            => events.Select(x => x as IEvent ?? new EventRegistry().BuildEvent(x)).ToArray();
    }
}
