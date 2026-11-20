using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Polly;
using Polly.Retry;
using Shouldly;

namespace InMemoryEventStoreTests;

public record Flaky;

/// <summary>Fails each stream's event the way <see cref="FlakyFaults" /> says, then applies it.</summary>
public partial class FlakyTally
{
    public Guid Id { get; set; }
    public int Applied { get; set; }

    public void Apply(IEvent<Flaky> e)
    {
        FlakyFaults.Visit(e.StreamId);
        Applied++;
    }
}

/// <summary>The same faults, through an event projection, which the daemon runs on its plain projection execution.</summary>
public partial class FlakyEventProjection : EventProjection
{
    public FlakyEventProjection()
    {
        Name = "FlakyEvents";
    }

    public void Project(IEvent<Flaky> e, IInMemoryDocumentSession ops)
    {
        FlakyFaults.Visit(e.StreamId);
    }
}

public static class FlakyFaults
{
    private static readonly ConcurrentDictionary<Guid, Fault> _faults = new();

    public static void Fail(Guid stream, int times, Func<Exception> exception)
        => _faults[stream] = new Fault(times, exception);

    public static int Attempts(Guid stream) => _faults.TryGetValue(stream, out var fault) ? fault.Attempts : 0;

    public static void Forget(Guid stream) => _faults.TryRemove(stream, out _);

    public static void Visit(Guid stream)
    {
        if (!_faults.TryGetValue(stream, out var fault)) return;

        var attempt = Interlocked.Increment(ref fault.Attempts);
        if (attempt <= fault.Times) throw fault.Exception();
    }

    private sealed class Fault(int times, Func<Exception> exception)
    {
        public readonly int Times = times;
        public readonly Func<Exception> Exception = exception;
        public int Attempts;
    }
}

/// <summary>
/// jasperfx#1256: a batch whose build fails transiently is built again through the store's resilience pipeline, under
/// every error policy, and the shard pauses as transient only once the pipeline gives up. A failure that is not
/// transient is still the error policy's to park, pause or skip.
/// </summary>
public class transient_batch_build_failures : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Retries at once: there is nothing to wait for on this store
    private static ResiliencePipeline retriesTwice() => new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<TimeoutException>(),
            MaxRetryAttempts = 2,
            Delay = TimeSpan.Zero
        })
        .Build();

    private static Exception transient() => new TimeoutException("the pool ran dry");
    private static Exception poison() => new InvalidOperationException("a bug in the projection");

    private readonly InMemoryDocumentStore theStore = new();
    private readonly List<Guid> theStreams = new();
    private InMemoryEventDatabase theDatabase = null!;
    private IProjectionDaemon? theDaemon;
    private Task<ShardState> thePause = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Flaky));
        theDatabase = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in theStreams) FlakyFaults.Forget(stream);

        if (theDaemon != null)
        {
            await theDaemon.StopAllAsync();
            theDaemon.Dispose();
        }
    }

    private void aggregation() => theStore.Projections.Snapshot<FlakyTally>(SnapshotLifecycle.Async);

    private void eventProjection() => theStore.Projections.Add(new FlakyEventProjection(), ProjectionLifecycle.Async);

    // Every stream is appended before the daemon starts, so they all land in its first batch
    private async Task<Guid[]> appendAsync(int count)
    {
        var streams = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        theStreams.AddRange(streams);

        await using var session = theStore.LightweightSession();
        foreach (var stream in streams) session.Events.StartStream<FlakyTally>(stream, new Flaky());
        await session.SaveChangesAsync(Cancellation);

        return streams;
    }

    private async Task startAsync(ProjectionErrorAction policy, ResiliencePipeline pipeline)
    {
        theStore.ContinuousErrors.ApplyErrors = policy;
        theStore.ResiliencePipeline = pipeline;

        // Watching before anything runs, so a pause can't be missed
        thePause = theDatabase.Tracker.WaitForShardCondition(x => x.Action == ShardAction.Paused, "a paused shard",
            Timeout);

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        await theDaemon.StartAllAsync();
    }

    private int deadLetters() => theStore.DeadLetters(theStore.AllShards().Single().Name, null).Count;

    private async Task<int> appliedAsync(Guid stream)
    {
        await using var query = theStore.QuerySession();
        return (await query.LoadAsync<FlakyTally>(stream, Cancellation)).ShouldNotBeNull().Applied;
    }

    [Theory]
    [InlineData(ProjectionErrorAction.Park)]
    [InlineData(ProjectionErrorAction.Pause)]
    [InlineData(ProjectionErrorAction.Skip)]
    public async Task a_single_transient_failure_is_built_again(ProjectionErrorAction policy)
    {
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], 1, transient);

        await startAsync(policy, retriesTwice());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        FlakyFaults.Attempts(streams[0]).ShouldBe(2);
        foreach (var stream in streams) (await appliedAsync(stream)).ShouldBe(1);

        thePause.IsCompleted.ShouldBeFalse();
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();
        deadLetters().ShouldBe(0);
    }

    [Theory]
    [InlineData(ProjectionErrorAction.Park)]
    [InlineData(ProjectionErrorAction.Pause)]
    [InlineData(ProjectionErrorAction.Skip)]
    public async Task several_transient_slice_failures_are_built_again(ProjectionErrorAction policy)
    {
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], 1, transient);
        FlakyFaults.Fail(streams[1], 2, transient);

        await startAsync(policy, retriesTwice());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        // The whole batch is built again each time, so the stream that failed once is applied on every build
        FlakyFaults.Attempts(streams[0]).ShouldBe(3);
        FlakyFaults.Attempts(streams[1]).ShouldBe(3);
        foreach (var stream in streams) (await appliedAsync(stream)).ShouldBe(1);

        thePause.IsCompleted.ShouldBeFalse();
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();
        deadLetters().ShouldBe(0);
    }

    [Fact]
    public async Task a_transient_failure_of_a_plain_projection_is_built_again()
    {
        eventProjection();
        var streams = await appendAsync(2);
        FlakyFaults.Fail(streams[0], 2, transient);

        await startAsync(ProjectionErrorAction.Park, retriesTwice());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        // Built twice more, and the third build committed: the shard is caught up and running. (An event projection
        // only writes documents inline on this store, so its progression is what shows the batch landed.)
        FlakyFaults.Attempts(streams[0]).ShouldBe(3);
        theStore.ProgressionFor(theStore.AllShards().Single().Name.Identity).ShouldBe(theStore.HighestEventSequence());
        thePause.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProjectionErrorAction.Park)]
    [InlineData(ProjectionErrorAction.Pause)]
    public async Task a_transient_failure_that_outlasts_the_pipeline_pauses_as_transient(ProjectionErrorAction policy)
    {
        aggregation();
        var streams = await appendAsync(2);
        FlakyFaults.Fail(streams[0], int.MaxValue, transient);

        await startAsync(policy, retriesTwice());

        var paused = await thePause;
        paused.Failure.ShouldNotBeNull().IsTransient.ShouldBeTrue();
        paused.Failure.RootExceptionType.ShouldBe(typeof(TimeoutException).FullName);

        // Once, and the two retries the pipeline allows
        FlakyFaults.Attempts(streams[0]).ShouldBe(3);
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task skip_builds_several_transient_failures_again_only_as_often_as_the_pipeline_retries()
    {
        // This used to go round for good: an AggregateException with no event in it to skip built the batch again
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], int.MaxValue, transient);
        FlakyFaults.Fail(streams[1], int.MaxValue, transient);

        await startAsync(ProjectionErrorAction.Skip, retriesTwice());

        var paused = await thePause;
        paused.Failure.ShouldNotBeNull().IsTransient.ShouldBeTrue();
        paused.Exception.ShouldBeOfType<AggregateException>().InnerExceptions.Count.ShouldBe(2);

        FlakyFaults.Attempts(streams[0]).ShouldBe(3);
        FlakyFaults.Attempts(streams[1]).ShouldBe(3);

        // Nothing was the events' fault, so nothing was skipped
        deadLetters().ShouldBe(0);
    }

    [Fact]
    public async Task skip_without_a_pipeline_pauses_several_transient_failures_at_once()
    {
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], int.MaxValue, transient);
        FlakyFaults.Fail(streams[1], int.MaxValue, transient);

        await startAsync(ProjectionErrorAction.Skip, ResiliencePipeline.Empty);

        (await thePause).Failure.ShouldNotBeNull().IsTransient.ShouldBeTrue();
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
        FlakyFaults.Attempts(streams[1]).ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_that_is_not_transient_still_parks()
    {
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], int.MaxValue, poison);

        await startAsync(ProjectionErrorAction.Park, retriesTwice());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        // Parked on the first build, never handed to the pipeline
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation))
            .ShouldHaveSingleItem().Identity.ShouldBe(streams[0]);
        (await appliedAsync(streams[1])).ShouldBe(1);
        thePause.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task a_failure_that_is_not_transient_still_pauses()
    {
        aggregation();
        var streams = await appendAsync(2);
        FlakyFaults.Fail(streams[0], int.MaxValue, poison);

        await startAsync(ProjectionErrorAction.Pause, retriesTwice());

        var paused = await thePause;
        paused.Failure.ShouldNotBeNull().IsTransient.ShouldBeFalse();
        paused.Failure.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_that_is_not_transient_is_still_skipped()
    {
        aggregation();
        var streams = await appendAsync(3);
        FlakyFaults.Fail(streams[0], int.MaxValue, poison);
        FlakyFaults.Fail(streams[1], int.MaxValue, poison);

        await startAsync(ProjectionErrorAction.Skip, retriesTwice());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        // Each was skipped on the first build that saw it, and the build that skipped them ran once more
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
        FlakyFaults.Attempts(streams[1]).ShouldBe(1);
        deadLetters().ShouldBe(2);
        (await appliedAsync(streams[2])).ShouldBe(1);
        thePause.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProjectionErrorAction.Park)]
    [InlineData(ProjectionErrorAction.Pause)]
    [InlineData(ProjectionErrorAction.Skip)]
    public async Task with_the_default_empty_pipeline_a_transient_failure_pauses_as_transient_at_once(
        ProjectionErrorAction policy)
    {
        aggregation();
        var streams = await appendAsync(2);
        FlakyFaults.Fail(streams[0], 1, transient);

        theStore.ResiliencePipeline.ShouldBeSameAs(ResiliencePipeline.Empty);
        await startAsync(policy, theStore.ResiliencePipeline);

        (await thePause).Failure.ShouldNotBeNull().IsTransient.ShouldBeTrue();
        FlakyFaults.Attempts(streams[0]).ShouldBe(1);
        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Cancellation)).ShouldBeEmpty();
        deadLetters().ShouldBe(0);
    }
}
