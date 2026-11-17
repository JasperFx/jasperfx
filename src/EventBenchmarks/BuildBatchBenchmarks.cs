using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EventBenchmarks;

/// <summary>
/// jasperfx#1201: what the per-identity rebuild hook costs <see cref="AggregationRunner{TDoc,TId,TOperations,TQuerySession}.BuildBatchAsync" />
/// when nothing is requested. Run it on this build and on the build before the hook, interleaved: the
/// <c>Idle</c> case compiles against both. <c>IdleAfterARebuild</c> is the runner after one request has come and
/// gone, so its pending state is allocated but empty; it only exists after the hook.
/// </summary>
/// <remarks>
/// The store, batch and storage are minimal hand-written fakes, so what is measured is the runner itself: slicing
/// is done once in setup, every slice is a cache miss and a no-op store, and nothing is written.
/// </remarks>
[MemoryDiagnoser]
public class BuildBatchBenchmarks
{
    private AggregationRunner<Tally, Guid, FakeOps, FakeOps> _idle = null!;
    private AggregationRunner<Tally, Guid, FakeOps, FakeOps> _afterARebuild = null!;
    private EventRange _range = null!;

    [Params(100, 1000)]
    public int Events { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _idle = buildRunner();
        _afterARebuild = buildRunner();

        // The state a runner keeps once a request has been made and completed: allocated, holding nothing
        var field = typeof(AggregationRunner<Tally, Guid, FakeOps, FakeOps>).GetField("_identityRebuilds",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(_afterARebuild, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Name.Returns(new ShardName("Tally"));
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var streams = Enumerable.Range(0, Math.Max(Events / 10, 1)).Select(_ => Guid.NewGuid()).ToArray();
        var events = Enumerable.Range(1, Events).Select(i =>
        {
            var e = new Event<Ticked>(new Ticked()) { Sequence = i, StreamId = streams[i % streams.Length] };
            return (IEvent)e;
        }).ToList();

        _range = new EventRange(agent, 0, Events) { Events = events };

        var group = new SliceGroup<Tally, Guid>();
        foreach (var e in events) group.AddEvent(e.StreamId, e);
        _range.SliceAsync(new FixedSlicer(group)).AsTask().GetAwaiter().GetResult();
    }

    private static AggregationRunner<Tally, Guid, FakeOps, FakeOps> buildRunner()
    {
        var ops = new FakeOps();
        var batch = Substitute.For<IProjectionBatch<FakeOps, FakeOps>>();
        batch.SessionForTenant(Arg.Any<string>()).Returns(ops);

        var store = Substitute.For<IEventStore<FakeOps, FakeOps>>();
        store.StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(), Arg.Any<ShardExecutionMode>(),
                Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOps, FakeOps>>(batch));

        return new AggregationRunner<Tally, Guid, FakeOps, FakeOps>(store, Substitute.For<IEventDatabase>(),
            new TallyProjection(), SliceBehavior.Preprocess, new FixedSlicer(new SliceGroup<Tally, Guid>()),
            NullLogger.Instance);
    }

    [Benchmark(Baseline = true)]
    public async Task Idle()
    {
        var batch = await _idle.BuildBatchAsync(_range, ShardExecutionMode.Continuous, CancellationToken.None);
        await batch.DisposeAsync();
    }

    [Benchmark]
    public async Task IdleAfterARebuild()
    {
        var batch = await _afterARebuild.BuildBatchAsync(_range, ShardExecutionMode.Continuous, CancellationToken.None);
        await batch.DisposeAsync();
    }
}

public record Ticked;

public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

public class FakeOps : IStorageOperations
{
    private readonly TallyStorage _storage = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken)
        => Task.FromResult((IProjectionStorage<TDoc, TId>)(object)_storage);

    public bool EnableSideEffectsOnInlineProjections => false;

    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();
}

public class FixedSlicer(SliceGroup<Tally, Guid> group) : IEventSlicer
{
    public ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
        => new([group]);

    public ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
        => new([group]);
}

public class TallyStorage : IProjectionStorage<Tally, Guid>
{
    private static readonly IReadOnlyDictionary<Guid, Tally> _none = new Dictionary<Guid, Tally>();

    public string TenantId => "*DEFAULT*";
    public void SetIdentity(Tally document, Guid identity) => document.Id = identity;
    public Guid Identity(Tally document) => document.Id;
    public void HardDelete(Tally snapshot) { }
    public void UnDelete(Tally snapshot) { }
    public void Store(Tally snapshot) { }
    public void Delete(Guid identity) { }
    public void HardDelete(Tally snapshot, string tenantId) { }
    public void UnDelete(Tally snapshot, string tenantId) { }
    public void Store(Tally snapshot, Guid id, string tenantId) { }
    public void Delete(Guid identity, string tenantId) { }

    public Task<IReadOnlyDictionary<Guid, Tally>> LoadManyAsync(Guid[] identities, CancellationToken cancellationToken)
        => Task.FromResult(_none);

    public void StoreProjection(Tally aggregate, IEvent? lastEvent, AggregationScope scope) { }
    public void ArchiveStream(Guid sliceId, string tenantId) { }
    public Task<Tally> LoadAsync(Guid id, CancellationToken cancellation) => Task.FromResult(new Tally());
}

public class TallyProjection : IAggregationProjection<Tally, Guid, FakeOps, FakeOps>
{
    public ValueTask RaiseSideEffects(FakeOps operations, IEventSlice<Tally> slice) => ValueTask.CompletedTask;
    public AggregationScope Scope => AggregationScope.SingleStream;
    public bool MatchesAnyDeleteType(IReadOnlyList<IEvent> events) => false;
    public Tally ApplyMetadata(Tally aggregate, IEvent @event) => aggregate;

    public ValueTask<(Tally?, ActionType)> DetermineActionAsync(FakeOps session, Tally? snapshot, Guid identity,
        IIdentitySetter<Tally, Guid> identitySetter, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        var tally = snapshot ?? new Tally { Id = identity };
        tally.Count += events.Count;
        return new ValueTask<(Tally?, ActionType)>((tally, ActionType.Store));
    }

    public Type[] AllEventTypes { get; } = [typeof(Ticked)];
    public string Name => "Tally";

    public (IEvent?, Tally?) TryApplyMetadata(IReadOnlyList<IEvent> events, Tally? aggregate, Guid id,
        IIdentitySetter<Tally, Guid> identitySetter) => (events.LastOrDefault(), aggregate);

    public AsyncOptions Options { get; } = new();
    public IEventSlicer BuildSlicer(FakeOps session) => throw new NotSupportedException();
    public void StartBatch() { }
    public ValueTask EndBatchAsync() => ValueTask.CompletedTask;

    public Task EnrichEventsAsync(SliceGroup<Tally, Guid> group, FakeOps querySession,
        CancellationToken cancellation) => Task.CompletedTask;
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
