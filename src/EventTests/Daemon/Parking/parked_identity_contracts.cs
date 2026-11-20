using System.Globalization;
using EventTests.Projections;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon.Parking;

public readonly record struct OrderNumber(Guid Value);

public readonly record struct OrderCode(string Value);

public readonly record struct SeatNumber(int Value);

public record OrderPlaced(Guid OrderId);

public record OrderShipped(Guid OrderId);

public class OrderSummary
{
    public Guid Id { get; set; }
    public int Events { get; set; }
}

public partial class OrderSummaryByDelegate : JasperFxMultiStreamProjectionBase<OrderSummary, Guid, FakeOperations, FakeSession>
{
    public OrderSummaryByDelegate()
    {
        Identity<OrderPlaced>(x => x.OrderId);
    }

    public void Apply(OrderSummary doc, OrderPlaced e) => doc.Events++;
}

public partial class OrderSummaryByStream : JasperFxMultiStreamProjectionBase<OrderSummary, Guid, FakeOperations, FakeSession>
{
    public OrderSummaryByStream()
    {
        IdentityFromStream<OrderPlaced>();
        IdentityFromStream<OrderShipped>();
    }

    public void Apply(OrderSummary doc, OrderPlaced e) => doc.Events++;
    public void Apply(OrderSummary doc, OrderShipped e) => doc.Events++;
}

public partial class OrderSummaryByTag : JasperFxMultiStreamProjectionBase<OrderSummary, Guid, FakeOperations, FakeSession>
{
    public OrderSummaryByTag()
    {
        IdentityFromTag<OrderNumber>();
        IdentityFromStream<OrderShipped>();
    }

    public void Apply(OrderSummary doc, OrderPlaced e) => doc.Events++;
    public void Apply(OrderSummary doc, OrderShipped e) => doc.Events++;
}

public partial class OrderSummaryMixed : JasperFxMultiStreamProjectionBase<OrderSummary, Guid, FakeOperations, FakeSession>
{
    public OrderSummaryMixed()
    {
        IdentityFromTag<OrderNumber>();
        Identity<OrderShipped>(x => x.OrderId);
    }

    public void Apply(OrderSummary doc, OrderPlaced e) => doc.Events++;
    public void Apply(OrderSummary doc, OrderShipped e) => doc.Events++;
}

public class TenantSummary
{
    public string Id { get; set; } = "";
    public int Events { get; set; }
}

public partial class TenantRollupSummary : JasperFxMultiStreamProjectionBase<TenantSummary, string, FakeOperations, FakeSession>
{
    public TenantRollupSummary()
    {
        RollUpByTenant();
    }

    public void Apply(TenantSummary doc, OrderPlaced e) => doc.Events++;
}

public class parked_identity_contracts
{
    [Fact]
    public void the_defaults_do_not_park()
    {
        var options = new ErrorHandlingOptions();
        options.ApplyErrors.ShouldBe(ProjectionErrorAction.Pause);
        options.SerializationErrors.ShouldBe(ProjectionErrorAction.Pause);
        options.MaxParkedIdentitiesPerShard.ShouldBe(1000);
        options.ParkedTripMinimum.ShouldBe(20);
        options.ParkedTripFraction.ShouldBe(0.5);
        options.ParkedReplayBatchSize.ShouldBe(10);
    }

    [Fact]
    public void the_skip_flags_map_onto_the_actions_both_ways()
    {
        var options = new ErrorHandlingOptions { SkipApplyErrors = true, SkipSerializationErrors = true };
        options.ApplyErrors.ShouldBe(ProjectionErrorAction.Skip);
        options.SerializationErrors.ShouldBe(ProjectionErrorAction.Skip);

        options.SkipApplyErrors = false;
        options.ApplyErrors.ShouldBe(ProjectionErrorAction.Pause);

        options.ApplyErrors = ProjectionErrorAction.Park;
        options.SkipApplyErrors.ShouldBeFalse();

        options.SerializationErrors = ProjectionErrorAction.Skip;
        options.SkipSerializationErrors.ShouldBeTrue();
    }

    [Fact]
    public void the_descriptor_carries_the_actions()
    {
        var descriptor = ProjectionErrorHandlingDescriptor.For(new ErrorHandlingOptions
        {
            ApplyErrors = ProjectionErrorAction.Park,
            SkipUnknownEvents = true,
            MaxParkedIdentitiesPerShard = 50
        });

        descriptor.ApplyErrors.ShouldBe("Park");
        descriptor.SerializationErrors.ShouldBe("Pause");
        descriptor.SkipApplyErrors.ShouldBeFalse();
        descriptor.SkipUnknownEvents.ShouldBeTrue();
        descriptor.MaxParkedIdentitiesPerShard.ShouldBe(50);
    }

    public static TheoryData<object, string, string> Identities => new()
    {
        { Guid.Parse("8b1d3e0c-5a5b-4c1e-9a0e-2a3b4c5d6e7f"), "8b1d3e0c-5a5b-4c1e-9a0e-2a3b4c5d6e7f", "guid" },
        { "order/12", "order/12", "string" },
        { 1234567, "1234567", "int" },
        { 9876543210L, "9876543210", "long" },
        { new OrderNumber(Guid.Parse("8b1d3e0c-5a5b-4c1e-9a0e-2a3b4c5d6e7f")), "8b1d3e0c-5a5b-4c1e-9a0e-2a3b4c5d6e7f", "guid" },
        { new OrderCode("ABC"), "ABC", "string" },
        { new SeatNumber(-42), "-42", "int" }
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void identity_text_round_trips(object identity, string text, string type)
    {
        ParkedIdentityText.Format(identity).ShouldBe(text);
        ParkedIdentityText.TypeNameOf(identity).ShouldBe(type);
        ParkedIdentityText.Parse(text, identity.GetType()).ShouldBe(identity);
    }

    [Fact]
    public void identity_text_ignores_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Formats negative numbers with a different minus sign
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            ParkedIdentityText.Format(-1234567L).ShouldBe("-1234567");
            ParkedIdentityText.Parse<long>("-1234567").ShouldBe(-1234567L);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void a_parked_limit_is_its_own_failure_category()
    {
        var shard = ShardName.Compose("Trip");
        var failure = ShardFailure.For(new ParkedLimitReachedException(shard, "too many", null), DateTimeOffset.UtcNow);

        failure.Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);
    }

    [Fact]
    public void a_deserialization_failure_can_name_its_stream_and_tenant()
    {
        var stream = Guid.NewGuid();
        IEventFailureContext context = new EventDeserializationFailureException(12, "trip_started", new Exception())
        {
            StreamId = stream,
            TenantId = "red"
        };

        context.StreamId.ShouldBe(stream);
        context.TenantId.ShouldBe("red");
        context.StreamKey.ShouldBeNull();
    }

    [Fact]
    public void a_single_stream_projection_can_park()
    {
        var access = new SingleStreamProjection<OrderSummary, Guid>().IdentityAccess;

        access.Class.ShouldBe(IdentityAccessClass.SingleStream);
        access.Exact.ShouldBeTrue();
        access.CanPark.ShouldBeTrue();
    }

    [Fact]
    public void a_delegate_makes_a_multi_stream_projection_scan_only()
    {
        var access = new OrderSummaryByDelegate().IdentityAccess;

        access.Class.ShouldBe(IdentityAccessClass.ScanOnly);
        access.Exact.ShouldBeFalse();
        access.CanPark.ShouldBeFalse();
        access.Reason.ShouldContain("Identity<OrderPlaced>");
        access.Reason.ShouldContain("IdentityFromTag");
    }

    [Fact]
    public void declared_stream_rules_are_exact()
    {
        var access = new OrderSummaryByStream().IdentityAccess;

        access.Class.ShouldBe(IdentityAccessClass.Stream);
        access.Exact.ShouldBeTrue();

        // marten#6003 phase 2: every exact class parks
        access.CanPark.ShouldBeTrue();
        access.Source.ShouldBe(IdentityEventSource.Stream);
    }

    [Fact]
    public void declared_tag_rules_are_exact_and_ask_for_the_tags()
    {
        var projection = new OrderSummaryByTag();

        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.Tag);
        projection.IdentityAccess.Exact.ShouldBeTrue();
        projection.IncludeEventTags.ShouldBeTrue();
    }

    // ---- marten#6003 phase 2: the slice index

    [Fact]
    public void the_slice_index_makes_a_delegate_grouping_findable()
    {
        var projection = new OrderSummaryByDelegate();
        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.ScanOnly);
        projection.IdentityAccess.Reason.ShouldContain("UseSliceIndex()");

        projection.Options.UseSliceIndex();

        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.SliceIndex);
        projection.IdentityAccess.CanPark.ShouldBeTrue();
        projection.IdentityAccess.Source.ShouldBe(IdentityEventSource.SliceIndex);
        projection.IdentityAccess.Reason.ShouldContain("ForParking");
    }

    [Fact]
    public void the_slice_index_keeps_seven_days_for_parking_by_default()
    {
        var options = new AsyncOptions().UseSliceIndex();

        options.Retention.ShouldBe(SliceIndexRetention.ForParking(TimeSpan.FromDays(7)));
        options.Retention.IsFull.ShouldBeFalse();
        options.CustomGroupingReplay.ShouldBe(SliceIndexGroupingReplay.AsRecorded);
        options.PruneInterval.ShouldBe(TimeSpan.FromHours(1));

        options.KeepEverything().Retention.IsFull.ShouldBeTrue();
        options.RerunCustomGroupingOnReplay().CustomGroupingReplay.ShouldBe(SliceIndexGroupingReplay.RerunGrouping);

        new AsyncOptions().UseSliceIndex(SliceIndexRetention.Full).Retention.ShouldBeSameAs(SliceIndexRetention.Full);
        Should.Throw<ArgumentOutOfRangeException>(() => SliceIndexRetention.ForParking(TimeSpan.Zero));
    }

    [Fact]
    public void using_the_slice_index_twice_keeps_one_set_of_options()
    {
        var options = new AsyncOptions();
        options.UseSliceIndex().ShouldBeSameAs(options.UseSliceIndex());
        options.SliceIndex.ShouldNotBeNull();
    }

    [Fact]
    public void grouping_across_tenants_needs_the_slice_index()
    {
        var projection = new OrderSummaryByStream { TenancyGrouping = TenancyGrouping.AcrossTenants };
        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.ScanOnly);

        projection.Options.UseSliceIndex();
        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.SliceIndex);
    }

    [Fact]
    public void rolling_up_by_tenant_stays_scan_only_with_the_slice_index()
    {
        var projection = new TenantRollupSummary();
        projection.Options.UseSliceIndex();

        projection.IdentityAccess.Class.ShouldBe(IdentityAccessClass.ScanOnly);
    }

    [Fact]
    public void one_delegate_among_declared_rules_is_scan_only()
    {
        var access = new OrderSummaryMixed().IdentityAccess;

        access.Class.ShouldBe(IdentityAccessClass.ScanOnly);
        access.Reason.ShouldContain("Identity<OrderShipped>");
    }

    [Fact]
    public void custom_grouping_is_scan_only()
    {
        new UsesCustomSlicerDayProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.ScanOnly);
    }

    [Fact]
    public void a_composite_does_not_park()
    {
        var composite = new JasperFx.Events.Projections.Composite.CompositeProjection<FakeOperations, FakeSession>("both");

        composite.IdentityAccess.Class.ShouldBe(IdentityAccessClass.NotSupported);
        composite.IdentityAccess.CanPark.ShouldBeFalse();
        composite.IdentityAccess.Reason.ShouldContain("pauses");
    }

    [Fact]
    public async Task identity_from_stream_groups_by_the_events_stream()
    {
        var slicer = new EventSlicer<OrderSummary, Guid, FakeSession>().IdentityFromStream<OrderPlaced>();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var events = new IEvent[]
        {
            new Event<OrderPlaced>(new OrderPlaced(Guid.NewGuid())) { StreamId = first },
            new Event<OrderPlaced>(new OrderPlaced(Guid.NewGuid())) { StreamId = second },
            new Event<OrderShipped>(new OrderShipped(Guid.NewGuid())) { StreamId = first }
        };

        var group = new SliceGroup<OrderSummary, Guid>();
        await slicer.SliceAsync(new FakeSession(), events, group);

        group.Slices[first].Events().Count.ShouldBe(1);
        group.Slices[second].Events().Count.ShouldBe(1);
    }

    [Fact]
    public async Task identity_from_tag_groups_by_every_tag_value_of_the_type()
    {
        var slicer = new EventSlicer<OrderSummary, Guid, FakeSession>().IdentityFromTag<OrderNumber>();

        var one = Guid.NewGuid();
        var two = Guid.NewGuid();

        var both = new Event<OrderPlaced>(new OrderPlaced(one));
        both.AddTag(new OrderNumber(one));
        both.AddTag(new OrderNumber(two));
        both.AddTag(new OrderCode("ignored"));

        var single = new Event<OrderShipped>(new OrderShipped(one));
        single.AddTag(new OrderNumber(one));

        var untagged = new Event<OrderShipped>(new OrderShipped(two));

        var group = new SliceGroup<OrderSummary, Guid>();
        await slicer.SliceAsync(new FakeSession(), new IEvent[] { both, single, untagged }, group);

        group.Slices[one].Events().Count.ShouldBe(2);
        group.Slices[two].Events().ShouldHaveSingleItem().ShouldBeSameAs(both);
        group.Slices.Count.ShouldBe(2);
    }

    [Fact]
    public void identity_from_tag_refuses_an_identity_the_tag_does_not_wrap()
    {
        Should.Throw<InvalidProjectionException>(() =>
            new EventSlicer<OrderSummary, Guid, FakeSession>().IdentityFromTag<OrderCode>());
    }
}
