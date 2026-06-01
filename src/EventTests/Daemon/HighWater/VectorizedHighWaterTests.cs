using JasperFx.Events.Daemon.HighWater;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// jasperfx#407 Phase 2: store-agnostic per-tenant high-water abstractions.
public class VectorizedHighWaterTests
{
    private static HighWaterStatistics stat(string? tenantId, long current, long highest)
        => new() { TenantId = tenantId, CurrentMark = current, HighestSequence = highest, LastMark = current };

    [Fact]
    public void high_water_vector_ceiling_lookup_per_tenant_and_global()
    {
        var vector = new HighWaterVector([stat("t1", 10, 12), stat("t2", 7, 7), stat(null, 99, 99)]);

        vector.CeilingFor("t1").ShouldBe(10);
        vector.CeilingFor("t2").ShouldBe(7);
        vector.CeilingFor(null).ShouldBe(99); // store-global slot
        vector.CeilingFor("missing").ShouldBeNull();

        vector.TenantCount.ShouldBe(2);
        vector.Global!.CurrentMark.ShouldBe(99);
    }

    [Fact]
    public async Task default_detector_has_no_tenant_dimension_and_returns_a_global_one_entry_vector()
    {
        // A store that doesn't partition events keeps its single-mark detector; the default vectorized
        // method must just wrap the store-global Detect() so it still compiles and behaves as before.
        IHighWaterDetector detector = new GlobalOnlyDetector(42);

        var vector = await detector.DetectForTenantsAsync(["t1", "t2"], CancellationToken.None);

        vector.TenantCount.ShouldBe(0);
        vector.Global.ShouldNotBeNull();
        vector.CeilingFor(null).ShouldBe(42);
    }

    [Fact]
    public async Task vectorized_poll_detects_each_tenant_independently_under_heterogeneous_states()
    {
        var detector = new FakeVectorDetector();
        var monitor = new VectorizedHighWaterMonitor(detector);
        monitor.PolledTenants.SetTenants(["stale", "flat", "advancing"]);

        // Baseline poll: every tenant caught up
        detector.Enqueue(new HighWaterVector([stat("stale", 10, 10), stat("flat", 5, 5), stat("advancing", 3, 3)]));
        // Second poll: stale tenant's mark is stuck behind a higher sequence (gap), flat is unchanged,
        // advancing has moved forward with more still pending.
        detector.Enqueue(new HighWaterVector([stat("stale", 10, 25), stat("flat", 5, 5), stat("advancing", 8, 12)]));

        await monitor.PollAsync(CancellationToken.None); // establish per-tenant baselines
        var readings = (await monitor.PollAsync(CancellationToken.None)).ToDictionary(x => x.TenantId);

        // Each tenant interpreted against ONLY its own previous reading -> no cross-tenant stall
        readings["stale"].Status.ShouldBe(HighWaterStatus.Stale);
        readings["flat"].Status.ShouldBe(HighWaterStatus.CaughtUp);
        readings["advancing"].Status.ShouldBe(HighWaterStatus.Changed);
    }

    [Fact]
    public async Task per_tenant_rebuild_ceiling_lookup_returns_that_tenants_mark()
    {
        var detector = new FakeVectorDetector();
        var monitor = new VectorizedHighWaterMonitor(detector);
        monitor.PolledTenants.SetTenants(["t1", "t2"]);

        detector.Enqueue(new HighWaterVector([stat("t1", 17, 17), stat("t2", 4, 9)]));
        await monitor.PollAsync(CancellationToken.None);

        monitor.CeilingFor("t1").ShouldBe(17);
        monitor.CeilingFor("t2").ShouldBe(4);
        monitor.CeilingFor("never-polled").ShouldBeNull();
    }

    [Fact]
    public async Task monitor_polls_only_currently_assigned_tenants()
    {
        var detector = new FakeVectorDetector();
        var monitor = new VectorizedHighWaterMonitor(detector);
        detector.EnqueueFactory(tenants => new HighWaterVector(tenants.Select(t => stat(t, 1, 1))));

        monitor.PolledTenants.SetTenants(["a", "b"]);
        await monitor.PollAsync(CancellationToken.None);
        detector.LastPolled.ShouldBe(["a", "b"], ignoreOrder: true);

        // Simulate Wolverine moving tenant "b"'s shard off this node
        monitor.PolledTenants.Deactivate("b");
        monitor.PolledTenants.Activate("c");
        await monitor.PollAsync(CancellationToken.None);
        detector.LastPolled.ShouldBe(["a", "c"], ignoreOrder: true);
    }

    [Fact]
    public async Task monitor_does_not_poll_when_no_tenants_are_assigned()
    {
        var detector = new FakeVectorDetector();
        var monitor = new VectorizedHighWaterMonitor(detector);

        var readings = await monitor.PollAsync(CancellationToken.None);

        readings.ShouldBeEmpty();
        detector.PollCount.ShouldBe(0);
    }

    [Fact]
    public void polled_tenant_set_activate_deactivate_semantics()
    {
        var set = new PolledTenantSet();

        set.Activate("a").ShouldBeTrue();
        set.Activate("a").ShouldBeFalse(); // already present
        set.Activate("b");
        set.Count.ShouldBe(2);
        set.IsPolled("a").ShouldBeTrue();

        set.Deactivate("a").ShouldBeTrue();
        set.Deactivate("a").ShouldBeFalse();
        set.IsPolled("a").ShouldBeFalse();

        set.SetTenants(["x", "y", "z"]);
        set.Count.ShouldBe(3);
        set.Snapshot().ShouldBe(["x", "y", "z"], ignoreOrder: true);
    }
}

internal class GlobalOnlyDetector : IHighWaterDetector
{
    private readonly long _mark;
    public GlobalOnlyDetector(long mark) => _mark = mark;

    public Uri DatabaseUri { get; } = new("fake://db");

    public Task<HighWaterStatistics> Detect(CancellationToken token)
        => Task.FromResult(new HighWaterStatistics { CurrentMark = _mark, HighestSequence = _mark });

    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);
}

internal class FakeVectorDetector : IHighWaterDetector
{
    private readonly Queue<HighWaterVector> _queued = new();
    private Func<IReadOnlyCollection<string>, HighWaterVector>? _factory;

    public Uri DatabaseUri { get; } = new("fake://db");
    public IReadOnlyCollection<string>? LastPolled { get; private set; }
    public int PollCount { get; private set; }

    // Represents a partitioned store
    public bool SupportsTenantPartitioning => true;

    public void Enqueue(HighWaterVector vector) => _queued.Enqueue(vector);
    public void EnqueueFactory(Func<IReadOnlyCollection<string>, HighWaterVector> factory) => _factory = factory;

    public Task<HighWaterStatistics> Detect(CancellationToken token) => Task.FromResult(new HighWaterStatistics());
    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Task.FromResult(new HighWaterStatistics());

    public Task<HighWaterVector> DetectForTenantsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken token)
    {
        PollCount++;
        LastPolled = tenantIds;
        if (_factory != null)
        {
            return Task.FromResult(_factory(tenantIds));
        }

        return Task.FromResult(_queued.Count > 1 ? _queued.Dequeue() : _queued.Peek());
    }

    public Task<HighWaterVector> DetectInSafeZoneForTenantsAsync(IReadOnlyCollection<string> tenantIds,
        CancellationToken token)
        => DetectForTenantsAsync(tenantIds, token);
}
