using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class TenantRollupSlicerTests
{
    private static IEvent MakeEvent<T>(T data, string tenantId, long sequence = 0) where T : notnull
    {
        return new Event<T>(data) { TenantId = tenantId, Sequence = sequence };
    }

    [Fact]
    public async Task slices_events_by_tenant_id()
    {
        var slicer = new TenantRollupSlicer<SimpleAggregate>();

        var e1 = MakeEvent(new AEvent(), "tenant-a", 1);
        var e2 = MakeEvent(new BEvent(), "tenant-b", 2);
        var e3 = MakeEvent(new AEvent(), "tenant-a", 3);

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new List<IEvent> { e1, e2, e3 }, group);

        group.Slices["tenant-a"].Events().ShouldBe([e1, e3]);
        group.Slices["tenant-b"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task slice_async_from_events_returns_single_group()
    {
        var slicer = new TenantRollupSlicer<SimpleAggregate>();

        var e1 = MakeEvent(new AEvent(), "tenant-a", 1);
        var e2 = MakeEvent(new BEvent(), "tenant-b", 2);
        var e3 = MakeEvent(new CEvent(), "tenant-a", 3);

        IEventSlicer eventSlicer = slicer;
        var groups = await eventSlicer.SliceAsync(new List<IEvent> { e1, e2, e3 });

        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, string>>().Single();
        group.TenantId.ShouldBe(StorageConstants.DefaultTenantId);

        group.Slices["tenant-a"].Events().ShouldBe([e1, e3]);
        group.Slices["tenant-b"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task single_tenant_produces_single_slice()
    {
        var slicer = new TenantRollupSlicer<SimpleAggregate>();

        var e1 = MakeEvent(new AEvent(), "my-tenant", 1);
        var e2 = MakeEvent(new BEvent(), "my-tenant", 2);

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new List<IEvent> { e1, e2 }, group);

        group.Slices.Count().ShouldBe(1);
        group.Slices["my-tenant"].Events().ShouldBe([e1, e2]);
    }

    [Fact]
    public async Task empty_events_produces_no_slices()
    {
        var slicer = new TenantRollupSlicer<SimpleAggregate>();

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new List<IEvent>(), group);

        group.Slices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task many_tenants_each_get_own_slice()
    {
        var slicer = new TenantRollupSlicer<SimpleAggregate>();

        var events = new List<IEvent>();
        for (int i = 0; i < 5; i++)
        {
            events.Add(MakeEvent(new AEvent(), $"tenant-{i}", i + 1));
        }

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events, group);

        group.Slices.Count().ShouldBe(5);
        for (int i = 0; i < 5; i++)
        {
            group.Slices[$"tenant-{i}"].Events().Count.ShouldBe(1);
        }
    }
}
