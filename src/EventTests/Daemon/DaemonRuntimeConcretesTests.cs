using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Polly;
using Shouldly;

namespace EventTests.Daemon;

public class SkippedEventsCountObserverTests
{
    [Fact]
    public void populates_skipped_count_on_high_water_mark_skip()
    {
        var state = new ShardState(ShardState.HighWaterMark, 100)
        {
            Action = ShardAction.Skipped,
            PreviousGoodMark = 90
        };

        new SkippedEventsCountObserver().OnNext(state);

        state.SkippedEventsCount.ShouldBe(10);
    }

    [Fact]
    public void ignores_non_skip_actions()
    {
        var state = new ShardState(ShardState.HighWaterMark, 100)
        {
            Action = ShardAction.Updated,
            PreviousGoodMark = 90
        };

        new SkippedEventsCountObserver().OnNext(state);

        state.SkippedEventsCount.ShouldBeNull();
    }

    [Fact]
    public void ignores_skips_for_non_high_water_mark_shards()
    {
        // Marten's superset guard: only the HighWaterMark shard's skips set the count.
        var state = new ShardState("Trip:All", 100)
        {
            Action = ShardAction.Skipped,
            PreviousGoodMark = 90
        };

        new SkippedEventsCountObserver().OnNext(state);

        state.SkippedEventsCount.ShouldBeNull();
    }

    [Fact]
    public void does_not_overwrite_an_existing_count()
    {
        var state = new ShardState(ShardState.HighWaterMark, 100)
        {
            Action = ShardAction.Skipped,
            PreviousGoodMark = 90,
            SkippedEventsCount = 7
        };

        new SkippedEventsCountObserver().OnNext(state);

        state.SkippedEventsCount.ShouldBe(7);
    }
}

public class ResilientEventLoaderTests
{
    private static readonly ShardName TheShard = new("Trip", "All", 1);

    private static EventRequest BuildRequest(ISubscriptionMetrics metrics)
        => new() { Name = TheShard, Metrics = metrics };

    [Fact]
    public async Task delegates_to_the_inner_loader_through_the_pipeline()
    {
        var page = new EventPage(0);
        var inner = new StubLoader(_ => Task.FromResult(page));
        var loader = new ResilientEventLoader(ResiliencePipeline.Empty, inner,
            Substitute.For<IEventDatabase>());

        var result = await loader.LoadAsync(BuildRequest(Substitute.For<ISubscriptionMetrics>()),
            CancellationToken.None);

        result.ShouldBeSameAs(page);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task tracks_loading_metrics()
    {
        var metrics = Substitute.For<ISubscriptionMetrics>();
        var loader = new ResilientEventLoader(ResiliencePipeline.Empty,
            new StubLoader(_ => Task.FromResult(new EventPage(0))), Substitute.For<IEventDatabase>());

        await loader.LoadAsync(BuildRequest(metrics), CancellationToken.None);

        metrics.Received(1).TrackLoading(Arg.Any<EventRequest>());
    }

    private sealed class StubLoader(Func<EventRequest, Task<EventPage>> handler) : IEventLoader
    {
        public int Calls { get; private set; }

        public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        {
            Calls++;
            return handler(request);
        }
    }
}
