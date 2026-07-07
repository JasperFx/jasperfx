using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#494 (epic #486 WS2): ThrottledEventLoader bounds concurrent LoadAsync executions
// against one database. These tests are fully gate-driven (TaskCompletionSource), never
// timing-driven — a load only completes when the test releases it, so the observed concurrency
// numbers are exact, not racy.
public class ThrottledEventLoaderTests
{
    [Fact]
    public async Task bounds_concurrent_loads_to_the_semaphore_size()
    {
        var inner = new GatedLoader();
        using var throttle = new SemaphoreSlim(2);
        var loader = new ThrottledEventLoader(inner, throttle);

        var loads = Enumerable.Range(0, 5)
            .Select(_ => loader.LoadAsync(request(), CancellationToken.None))
            .ToArray();

        // Exactly the semaphore size may start; the other three queue on the throttle.
        await inner.WaitForStartsAsync(2);
        inner.Started.ShouldBe(2);
        inner.Active.ShouldBe(2);

        // Each completion admits exactly one queued load. Sequenced start-by-start so the
        // test never races the admissions.
        inner.ReleaseOne();
        await inner.WaitForStartsAsync(3);
        inner.Started.ShouldBe(3);

        inner.ReleaseOne();
        await inner.WaitForStartsAsync(4);
        inner.ReleaseOne();
        await inner.WaitForStartsAsync(5);

        // Drain the final two in-flight loads
        inner.ReleaseOne();
        inner.ReleaseOne();
        await Task.WhenAll(loads);

        inner.Started.ShouldBe(5);
        inner.MaxActive.ShouldBe(2);
    }

    [Fact]
    public async Task releases_the_slot_when_the_inner_loader_throws()
    {
        var inner = new GatedLoader();
        using var throttle = new SemaphoreSlim(1);
        var loader = new ThrottledEventLoader(inner, throttle);

        var failing = loader.LoadAsync(request(), CancellationToken.None);
        await inner.WaitForStartsAsync(1);
        inner.FailOne(new InvalidOperationException("boom"));
        await Should.ThrowAsync<InvalidOperationException>(() => failing);

        // The slot must have been released — a subsequent load starts immediately.
        var next = loader.LoadAsync(request(), CancellationToken.None);
        await inner.WaitForStartsAsync(2);
        inner.ReleaseOne();
        (await next).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_queued_load_can_be_cancelled_without_consuming_a_slot()
    {
        var inner = new GatedLoader();
        using var throttle = new SemaphoreSlim(1);
        var loader = new ThrottledEventLoader(inner, throttle);

        var running = loader.LoadAsync(request(), CancellationToken.None);
        await inner.WaitForStartsAsync(1);

        using var cts = new CancellationTokenSource();
        var queued = loader.LoadAsync(request(), cts.Token);
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => queued);

        // The cancelled waiter never reached the inner loader...
        inner.Started.ShouldBe(1);

        // ...and the slot economy is intact: finishing the first load lets a new one start.
        inner.ReleaseOne();
        await running;
        var next = loader.LoadAsync(request(), CancellationToken.None);
        await inner.WaitForStartsAsync(2);
        inner.ReleaseOne();
        await next;
    }

    [Fact]
    public void daemon_settings_default_is_four()
    {
        new DaemonSettings().MaxConcurrentEventLoadsPerDatabase.ShouldBe(4);
    }

    private static EventRequest request() => new()
    {
        Floor = 0, HighWater = 10, BatchSize = 100,
        ErrorOptions = new ErrorHandlingOptions(),
        Runtime = new NulloDaemonRuntime(),
        Name = new ShardName("Throttled")
    };

    // A loader whose loads only complete when the test says so. Start notifications are
    // TCS-armed (WaitForStartsAsync), so assertions never race the loads.
    private sealed class GatedLoader: IEventLoader
    {
        private readonly object _lock = new();
        private readonly Queue<TaskCompletionSource<EventPage>> _inFlight = new();
        private readonly List<(int Threshold, TaskCompletionSource Signal)> _startWaiters = new();

        public int Started { get; private set; }
        public int Active { get; private set; }
        public int MaxActive { get; private set; }

        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            var gate = new TaskCompletionSource<EventPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                Started++;
                Active++;
                MaxActive = Math.Max(MaxActive, Active);
                _inFlight.Enqueue(gate);

                foreach (var waiter in _startWaiters.Where(w => Started >= w.Threshold).ToArray())
                {
                    waiter.Signal.TrySetResult();
                    _startWaiters.Remove(waiter);
                }
            }

            return gate.Task.ContinueWith(t =>
            {
                lock (_lock)
                {
                    Active--;
                }

                return t.GetAwaiter().GetResult();
            }, TaskScheduler.Default);
        }

        public Task WaitForStartsAsync(int threshold)
        {
            lock (_lock)
            {
                if (Started >= threshold)
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _startWaiters.Add((threshold, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        public void ReleaseOne()
        {
            TaskCompletionSource<EventPage> gate;
            lock (_lock)
            {
                gate = _inFlight.Dequeue();
            }

            gate.SetResult(new EventPage(0));
        }

        public void FailOne(Exception ex)
        {
            TaskCompletionSource<EventPage> gate;
            lock (_lock)
            {
                gate = _inFlight.Dequeue();
            }

            gate.SetException(ex);
        }

    }
}
