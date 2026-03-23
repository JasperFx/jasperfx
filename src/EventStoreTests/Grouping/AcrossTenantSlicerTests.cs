using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class AcrossTenantSlicerTests
{
    private static IEvent MakeEvent<T>(T data, string tenantId, long sequence = 0) where T : notnull
    {
        return new Event<T>(data) { TenantId = tenantId, Sequence = sequence };
    }

    [Fact]
    public async Task ignores_tenant_and_slices_by_identity()
    {
        var innerSlicer = new EventSlicer<SimpleAggregate, string, object>();
        innerSlicer.Identity<IColorEvent>(e => e.Color);
        var slicer = new AcrossTenantSlicer<SimpleAggregate, string, object>(new object(), innerSlicer);

        var e1 = MakeEvent(new Added { Number = 1, Color = "blue" }, "tenant-a", 1);
        var e2 = MakeEvent(new Added { Number = 2, Color = "blue" }, "tenant-b", 2);
        var e3 = MakeEvent(new Added { Number = 3, Color = "red" }, "tenant-a", 3);

        var groups = await slicer.SliceAsync(new List<IEvent> { e1, e2, e3 });

        // Should produce a single group regardless of tenants
        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, string>>().Single();
        group.TenantId.ShouldBe(StorageConstants.DefaultTenantId);

        // Events from different tenants land in the same slice
        group.Slices["blue"].Events().ShouldBe([e1, e2]);
        group.Slices["red"].Events().ShouldBe([e3]);
    }

    [Fact]
    public async Task empty_events_produces_single_empty_group()
    {
        var innerSlicer = new EventSlicer<SimpleAggregate, string, object>();
        innerSlicer.Identity<IColorEvent>(e => e.Color);
        var slicer = new AcrossTenantSlicer<SimpleAggregate, string, object>(new object(), innerSlicer);

        var groups = await slicer.SliceAsync(new List<IEvent>());

        // Still produces one group, just with no slices
        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, string>>().Single();
        group.Slices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task multiple_identities_across_tenants()
    {
        var innerSlicer = new EventSlicer<SimpleAggregate, string, object>();
        innerSlicer.Identities<ITaggedEvent>(e => e.Tags);
        var slicer = new AcrossTenantSlicer<SimpleAggregate, string, object>(new object(), innerSlicer);

        var e1 = MakeEvent(new Started { Tags = new[] { "blue", "green" } }, "tenant-a", 1);
        var e2 = MakeEvent(new Ended { Tags = new[] { "blue" } }, "tenant-b", 2);

        var groups = await slicer.SliceAsync(new List<IEvent> { e1, e2 });

        groups.Count.ShouldBe(1);
        var group = groups.OfType<SliceGroup<SimpleAggregate, string>>().Single();

        group.Slices["blue"].Events().ShouldBe([e1, e2]);
        group.Slices["green"].Events().ShouldBe([e1]);
    }
}
