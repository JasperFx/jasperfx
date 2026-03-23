using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class TenantedEventSlicerTests
{
    private static IEvent MakeEvent<T>(T data, string tenantId, Guid? streamId = null, long sequence = 0) where T : notnull
    {
        var e = new Event<T>(data)
        {
            TenantId = tenantId,
            StreamId = streamId ?? Guid.NewGuid(),
            Sequence = sequence
        };
        return e;
    }

    [Fact]
    public async Task groups_events_by_tenant_then_slices_by_stream()
    {
        var inner = new ByStream<SimpleAggregate, Guid>();
        var slicer = new TenantedEventSlicer<SimpleAggregate, Guid>(inner);

        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        var e1 = MakeEvent(new AEvent(), "tenant-a", stream1, 1);
        var e2 = MakeEvent(new BEvent(), "tenant-b", stream2, 2);
        var e3 = MakeEvent(new AEvent(), "tenant-a", stream1, 3);
        var e4 = MakeEvent(new CEvent(), "tenant-b", stream2, 4);

        var events = new List<IEvent> { e1, e2, e3, e4 };
        var groups = await slicer.SliceAsync(events);

        groups.Count.ShouldBe(2);

        var tenantA = groups.OfType<SliceGroup<SimpleAggregate, Guid>>()
            .Single(g => g.TenantId == "tenant-a");
        tenantA.Slices[stream1].Events().ShouldBe([e1, e3]);

        var tenantB = groups.OfType<SliceGroup<SimpleAggregate, Guid>>()
            .Single(g => g.TenantId == "tenant-b");
        tenantB.Slices[stream2].Events().ShouldBe([e2, e4]);
    }

    [Fact]
    public async Task single_tenant_produces_single_group()
    {
        var inner = new ByStream<SimpleAggregate, Guid>();
        var slicer = new TenantedEventSlicer<SimpleAggregate, Guid>(inner);

        var stream1 = Guid.NewGuid();
        var e1 = MakeEvent(new AEvent(), "tenant-a", stream1, 1);
        var e2 = MakeEvent(new BEvent(), "tenant-a", stream1, 2);

        var groups = await slicer.SliceAsync(new List<IEvent> { e1, e2 });

        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, Guid>>().Single();
        group.TenantId.ShouldBe("tenant-a");
        group.Slices[stream1].Events().ShouldBe([e1, e2]);
    }

    [Fact]
    public async Task slices_with_color_identity_across_tenants()
    {
        var innerSlicer = new EventSlicer<SimpleAggregate, string>();
        innerSlicer.Identity<IColorEvent>(e => e.Color);
        var slicer = new TenantedEventSlicer<SimpleAggregate, string>(innerSlicer);

        var e1 = MakeEvent(new Added { Number = 1, Color = "blue" }, "tenant-a", sequence: 1);
        var e2 = MakeEvent(new Added { Number = 2, Color = "blue" }, "tenant-b", sequence: 2);
        var e3 = MakeEvent(new Added { Number = 3, Color = "red" }, "tenant-a", sequence: 3);

        var groups = await slicer.SliceAsync(new List<IEvent> { e1, e2, e3 });

        groups.Count.ShouldBe(2);

        var tenantA = groups.OfType<SliceGroup<SimpleAggregate, string>>()
            .Single(g => g.TenantId == "tenant-a");
        tenantA.Slices["blue"].Events().ShouldBe([e1]);
        tenantA.Slices["red"].Events().ShouldBe([e3]);

        var tenantB = groups.OfType<SliceGroup<SimpleAggregate, string>>()
            .Single(g => g.TenantId == "tenant-b");
        tenantB.Slices["blue"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task force_single_tenancy_ignores_tenant_grouping()
    {
        var inner = new ByStream<SimpleAggregate, Guid>();
        var slicer = new TenantedEventSlicer<SimpleAggregate, Guid>(inner)
        {
            ForceSingleTenancy = true
        };

        var stream1 = Guid.NewGuid();
        var e1 = MakeEvent(new AEvent(), "tenant-a", stream1, 1);
        var e2 = MakeEvent(new BEvent(), "tenant-b", stream1, 2);

        var groups = await slicer.SliceAsync(new List<IEvent> { e1, e2 });

        // All events should be in a single group with default tenant
        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, Guid>>().Single();
        group.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        group.Slices[stream1].Events().ShouldBe([e1, e2]);
    }

    [Fact]
    public async Task empty_events_produces_no_groups()
    {
        var inner = new ByStream<SimpleAggregate, Guid>();
        var slicer = new TenantedEventSlicer<SimpleAggregate, Guid>(inner);

        var groups = await slicer.SliceAsync(new List<IEvent>());
        groups.Count.ShouldBe(0);
    }

    [Fact]
    public async Task three_tenants_each_with_multiple_streams()
    {
        var inner = new ByStream<SimpleAggregate, Guid>();
        var slicer = new TenantedEventSlicer<SimpleAggregate, Guid>(inner);

        var streamA1 = Guid.NewGuid();
        var streamA2 = Guid.NewGuid();
        var streamB1 = Guid.NewGuid();
        var streamC1 = Guid.NewGuid();

        var events = new List<IEvent>
        {
            MakeEvent(new AEvent(), "tenant-a", streamA1, 1),
            MakeEvent(new BEvent(), "tenant-b", streamB1, 2),
            MakeEvent(new AEvent(), "tenant-a", streamA2, 3),
            MakeEvent(new CEvent(), "tenant-c", streamC1, 4),
            MakeEvent(new BEvent(), "tenant-a", streamA1, 5),
        };

        var groups = await slicer.SliceAsync(events);

        groups.Count.ShouldBe(3);

        var tenantA = groups.OfType<SliceGroup<SimpleAggregate, Guid>>()
            .Single(g => g.TenantId == "tenant-a");
        tenantA.Slices.Count().ShouldBe(2); // two streams
        tenantA.Slices[streamA1].Events().Count.ShouldBe(2);
        tenantA.Slices[streamA2].Events().Count.ShouldBe(1);
    }
}
