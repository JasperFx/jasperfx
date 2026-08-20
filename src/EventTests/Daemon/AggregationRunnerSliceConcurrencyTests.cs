using EventTests.Projections;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#683: AggregationRunner applies every slice in a range through a fixed 10-wide Block, and every
// one of them gets the SAME IProjectionStorage instance. That is what the products' own document storage is
// built for, and it is not what an EF Core storage can take -- it wraps one DbContext per tenant/batch, and
// a DbContext is not thread-safe (marten#5266: InvalidOperationException out of Dictionary.TryInsert,
// NullReferenceException out of ChangeDetector.DetectChanges).
//
// IProjectionStorage.IsThreadSafe lets the storage say so, and the runner then applies its slices one at a
// time. These tests observe the fan-out directly by counting how many applies are in flight at once, because
// that is the only thing that actually changed -- what a slice DOES is identical on both routes.
public class AggregationRunnerSliceConcurrencyTests
{
    private const int SliceCount = 20;

    private readonly IAggregationProjection<User, Guid, FakeOperations, FakeSession> theProjection =
        Substitute.For<IAggregationProjection<User, Guid, FakeOperations, FakeSession>>();

    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventSlicer theSlicer = Substitute.For<IEventSlicer>();
    private readonly ConcurrencyProbeStorage theStorage = new();
    private readonly AggregationRunner<User, Guid, FakeOperations, FakeSession> theRunner;

    public AggregationRunnerSliceConcurrencyTests()
    {
        var snapshot = new User("Beast", "Hank McCoy");

        theProjection.Options.Returns(new AsyncOptions());
        theProjection.Scope.Returns(AggregationScope.MultiStream);
        theProjection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(false);
        // The overlap is measured here rather than inside the storage, and it matters: this is an await,
        // so ten concurrent applies can be in flight without ten OS threads. Measuring at a blocking call
        // instead makes the concurrent route depend on the thread pool injecting threads, which a small CI
        // runner does not do quickly enough -- it measured a max concurrency of 1 and failed the fan-out
        // fact on net10.0 while passing on net9.0.
        theProjection
            .DetermineActionAsync(Arg.Any<FakeSession>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<(User?, ActionType)>(theStorage.TrackApplyAsync(snapshot)));
        theProjection
            .TryApplyMetadata(Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<User?>(), Arg.Any<Guid>(),
                Arg.Any<IProjectionStorage<User, Guid>>())
            .Returns(((IEvent?)null, snapshot));

        var batch = Substitute.For<IProjectionBatch<FakeOperations, FakeSession>>();
        batch.SessionForTenant(Arg.Any<string>()).Returns(new FakeOperations { ProjectionStorage = theStorage });
        theStore
            .StartProjectionBatchAsync(Arg.Any<EventRange>(), Arg.Any<IEventDatabase>(),
                Arg.Any<ShardExecutionMode>(), Arg.Any<AsyncOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IProjectionBatch<FakeOperations, FakeSession>>(batch));

        theRunner = new AggregationRunner<User, Guid, FakeOperations, FakeSession>(
            theStore,
            Substitute.For<IEventDatabase>(),
            theProjection,
            SliceBehavior.JustInTime,
            theSlicer,
            NullLogger.Instance);
    }

    private async Task buildBatchAsync()
    {
        var group = new SliceGroup<User, Guid>("foo");
        for (var i = 0; i < SliceCount; i++)
        {
            var id = Guid.NewGuid();
            group.Slices.Fill(id, new EventSlice<User, Guid>(id, "foo",
                new IEvent[] { new Event<AEvent>(new AEvent()) }));
        }

        theSlicer.SliceAsync(Arg.Any<EventRange>())
            .Returns(new ValueTask<IReadOnlyList<object>>(new object[] { group }));

        var agent = Substitute.For<ISubscriptionAgent>();
        agent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());

        var range = new EventRange(agent, 0, 100) { BatchBehavior = BatchBehavior.Individual };

        await theRunner.BuildBatchAsync(range, ShardExecutionMode.Continuous, CancellationToken.None);
    }

    /// <remarks>
    /// The regression. One slice at a time, and nothing weaker: "fewer than ten" would still be a data race
    /// against a DbContext.
    /// </remarks>
    [Fact]
    public async Task a_storage_that_is_not_thread_safe_gets_its_slices_applied_one_at_a_time()
    {
        theStorage.IsThreadSafe = false;

        await buildBatchAsync();

        theStorage.MaxConcurrency.ShouldBe(1);
        theStorage.Applied.ShouldBe(SliceCount);
    }

    /// <remarks>
    /// The other half, and the reason the seam is on the storage rather than a store-wide switch: the
    /// products' own document storage keeps the fan-out it was built for. Asserted as "more than one at
    /// once" rather than a specific width, since the scheduler decides how many of the ten actually overlap.
    /// </remarks>
    [Fact]
    public async Task a_thread_safe_storage_still_has_its_slices_applied_concurrently()
    {
        theStorage.IsThreadSafe = true;

        await buildBatchAsync();

        theStorage.MaxConcurrency.ShouldBeGreaterThan(1);
        theStorage.Applied.ShouldBe(SliceCount);
    }

    /// <remarks>
    /// The default is what every existing storage gets without changing a line, so it has to be the
    /// concurrent one — the seam is additive, not a new obligation.
    /// </remarks>
    [Fact]
    public void the_contract_defaults_to_thread_safe()
    {
        IProjectionStorage<User, Guid> storage = new PlainStorage();

        storage.IsThreadSafe.ShouldBeTrue();
    }

    /// <remarks>
    /// ⚠️ Pinned because it is a trap for the downstream adopters, all of whom are about to write tests
    /// around this seam. NSubstitute proxies a default interface member rather than inheriting it, so a
    /// substituted storage answers <c>default(bool)</c> — <see langword="false" /> — and silently takes the
    /// serial route. Harmless (serial is always correct) but it means a substitute cannot be used to
    /// exercise the concurrent route, and cannot be trusted to report what a real storage would. Every real
    /// implementation is a concrete class and gets the <see langword="true" /> default.
    /// </remarks>
    [Fact]
    public void a_substituted_storage_does_not_inherit_the_default()
    {
        Substitute.For<IProjectionStorage<User, Guid>>().IsThreadSafe.ShouldBeFalse();
    }

    /// <remarks>
    /// A slice that throws must not abandon the slices after it: the range still has to mark every slice's
    /// action, and the runner still has to surface the failure. The concurrent route got that from the
    /// block's OnError; the serial route has to reproduce it rather than let the exception escape the loop.
    /// </remarks>
    [Fact]
    public async Task a_failing_slice_on_the_serial_route_still_lets_the_rest_apply()
    {
        theStorage.IsThreadSafe = false;
        theStorage.FailOnApplyNumber = 3;

        var ex = await Should.ThrowAsync<DivideByZeroException>(buildBatchAsync());
        ex.Message.ShouldBe("slice 3");

        // Every slice was attempted, not just the ones before the failure.
        theStorage.Applied.ShouldBe(SliceCount);
        theStorage.MaxConcurrency.ShouldBe(1);
    }

    /// <remarks>
    /// Two or more failures aggregate rather than surfacing only the first, matching what the block route
    /// has always done.
    /// </remarks>
    [Fact]
    public async Task several_failing_slices_on_the_serial_route_aggregate()
    {
        theStorage.IsThreadSafe = false;
        theStorage.FailOnApplyNumber = 3;
        theStorage.AlsoFailOnApplyNumber = 7;

        var ex = await Should.ThrowAsync<AggregateException>(buildBatchAsync());

        ex.InnerExceptions.Count.ShouldBe(2);
        ex.InnerExceptions.Select(x => x.Message).ShouldBe(new[] { "slice 3", "slice 7" });
        theStorage.Applied.ShouldBe(SliceCount);
    }

    /// <summary>
    /// Records how many applies are in flight at once, and can fail a chosen one.
    /// </summary>
    private class ConcurrencyProbeStorage: IProjectionStorage<User, Guid>
    {
        private readonly object _lock = new();
        private int _inFlight;
        private int _stored;

        public bool IsThreadSafe { get; set; } = true;
        public int MaxConcurrency { get; private set; }
        public int Applied { get; private set; }

        public int? FailOnApplyNumber { get; set; }
        public int? AlsoFailOnApplyNumber { get; set; }

        public string TenantId => "foo";

        /// <summary>
        /// Called from the projection's apply, which awaits it. Records how many applies overlap.
        /// </summary>
        public async Task<(User?, ActionType)> TrackApplyAsync(User snapshot)
        {
            lock (_lock)
            {
                _inFlight++;
                Applied++;
                if (_inFlight > MaxConcurrency) MaxConcurrency = _inFlight;
            }

            try
            {
                // Long enough that a concurrent route has every slot occupied at once, and short enough
                // that the serial route's twenty applies still finish promptly.
                await Task.Delay(25);
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }
            }

            return (snapshot, ActionType.Store);
        }

        public void StoreProjection(User aggregate, IEvent? lastEvent, AggregationScope scope)
        {
            int number;
            lock (_lock)
            {
                _stored++;
                number = _stored;
            }

            if (number == FailOnApplyNumber || number == AlsoFailOnApplyNumber)
            {
                throw new DivideByZeroException($"slice {number}");
            }
        }

        public Task<IReadOnlyDictionary<Guid, User>> LoadManyAsync(Guid[] identities, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<Guid, User>>(new Dictionary<Guid, User>());

        public Task<User> LoadAsync(Guid id, CancellationToken cancellation) => Task.FromResult<User>(null!);

        public void SetIdentity(User document, Guid identity)
        {
        }

        public Guid Identity(User document) => Guid.Empty;

        public void HardDelete(User snapshot)
        {
        }

        public void UnDelete(User snapshot)
        {
        }

        public void Store(User snapshot)
        {
        }

        public void Delete(Guid identity)
        {
        }

        public void HardDelete(User snapshot, string tenantId)
        {
        }

        public void UnDelete(User snapshot, string tenantId)
        {
        }

        public void Store(User snapshot, Guid id, string tenantId)
        {
        }

        public void Delete(Guid identity, string tenantId)
        {
        }

        public void ArchiveStream(Guid sliceId, string tenantId)
        {
        }
    }

    /// <summary>
    /// Implements the contract without declaring
    /// <see cref="IProjectionStorage{TDoc,TId}.IsThreadSafe" />, so it reads the default — the shape every
    /// store has today. It cannot subclass the probe: interface member mapping is fixed at the type that
    /// declares the interface, so a member added further down would never be dispatched to.
    /// </summary>
    private class PlainStorage: IProjectionStorage<User, Guid>
    {
        public string TenantId => "foo";
        public void SetIdentity(User document, Guid identity) => throw new NotSupportedException();
        public Guid Identity(User document) => throw new NotSupportedException();
        public void HardDelete(User snapshot) => throw new NotSupportedException();
        public void UnDelete(User snapshot) => throw new NotSupportedException();
        public void Store(User snapshot) => throw new NotSupportedException();
        public void Delete(Guid identity) => throw new NotSupportedException();
        public void HardDelete(User snapshot, string tenantId) => throw new NotSupportedException();
        public void UnDelete(User snapshot, string tenantId) => throw new NotSupportedException();
        public void Store(User snapshot, Guid id, string tenantId) => throw new NotSupportedException();
        public void Delete(Guid identity, string tenantId) => throw new NotSupportedException();
        public void ArchiveStream(Guid sliceId, string tenantId) => throw new NotSupportedException();
        public void StoreProjection(User aggregate, IEvent? lastEvent, AggregationScope scope)
            => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, User>> LoadManyAsync(Guid[] identities, CancellationToken token)
            => throw new NotSupportedException();
        public Task<User> LoadAsync(Guid id, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
