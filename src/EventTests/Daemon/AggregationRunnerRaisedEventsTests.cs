using EventTests.Projections;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

// Events raised from RaiseSideEffects, followed from the projection through the runner to what the store's
// batch is told to write. Assertions read the actions after every one has been built, because a store
// executes them at commit and sees whatever the event instances say by then.
public class AggregationRunnerRaisedEventsTests
{
    private readonly RecordingBatch theBatch = new();

    private async Task applyAsync<TId>(EventSlice<User, TId> slice, AggregationScope scope,
        StreamIdentity streamIdentity, Action<IEventSlice<User>> raiseSideEffects) where TId : notnull
    {
        var registry = Substitute.For<IEventRegistry>();
        registry.StreamIdentity.Returns(streamIdentity);
        registry.TimeProvider.Returns(TimeProvider.System);
        registry.EventMappingFor(Arg.Any<Type>()).Returns(Substitute.For<IEventType>());

        var store = Substitute.For<IEventStore<FakeOperations, FakeSession>>();
        store.Registry.Returns(registry);

        var projection = Substitute.For<IAggregationProjection<User, TId, FakeOperations, FakeSession>>();
        projection.Scope.Returns(scope);
        projection.MatchesAnyDeleteType(Arg.Any<IReadOnlyList<IEvent>>()).Returns(false);

        var snapshot = new User("Beast", "Hank McCoy");
        projection
            .DetermineActionAsync(Arg.Any<FakeSession>(), Arg.Any<User?>(), Arg.Any<TId>(),
                Arg.Any<IProjectionStorage<User, TId>>(), Arg.Any<IReadOnlyList<IEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<(User?, ActionType)>((snapshot, ActionType.Store)));
        projection
            .TryApplyMetadata(Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<User?>(), Arg.Any<TId>(),
                Arg.Any<IProjectionStorage<User, TId>>())
            .Returns(((IEvent?)null, snapshot));
        projection
            .RaiseSideEffects(Arg.Any<FakeOperations>(), Arg.Any<TId>(), Arg.Any<IEventSlice<User>>())
            .Returns(call =>
            {
                raiseSideEffects(call.Arg<IEventSlice<User>>());
                return ValueTask.CompletedTask;
            });

        var storage = Substitute.For<IProjectionStorage<User, TId>>();
        storage.TenantId.Returns("foo");

        var runner = new AggregationRunner<User, TId, FakeOperations, FakeSession>(
            store,
            Substitute.For<IEventDatabase>(),
            projection,
            SliceBehavior.None,
            Substitute.For<IEventSlicer>(),
            NullLogger.Instance);

        await runner.ApplyChangesAsync(ShardExecutionMode.Continuous, theBatch, new FakeOperations(), slice,
            storage, Substitute.For<IAggregateCache<TId, User>>(), CancellationToken.None);
    }

    private static Event<AEvent> eventOnStream(Guid streamId, long version)
        => new(new AEvent()) { StreamId = streamId, Version = version };

    private static Event<AEvent> eventOnStream(string streamKey, long version)
        => new(new AEvent()) { StreamKey = streamKey, Version = version };

    [Fact]
    public async Task a_multi_stream_slice_writes_each_raised_event_only_to_the_stream_it_was_raised_to()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var a = new AEvent();
        var b = new BEvent();
        var c = new CEvent();

        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", [eventOnStream(Guid.NewGuid(), 4)]);

        await applyAsync(slice, AggregationScope.MultiStream, StreamIdentity.AsGuid, s =>
        {
            s.AppendEvent(left, a);
            s.AppendEvent(right, b);
            s.AppendEvent(left, c);
        });

        theBatch.QuickAppends.Count.ShouldBe(2);

        var leftAction = theBatch.QuickAppends.Single(x => x.Id == left);
        leftAction.Events.Select(x => x.Data).ShouldBe([a, c]);
        leftAction.Events.ShouldAllBe(x => x.StreamId == left);

        var rightAction = theBatch.QuickAppends.Single(x => x.Id == right);
        rightAction.Events.Select(x => x.Data).ShouldBe([b]);
        rightAction.Events.ShouldAllBe(x => x.StreamId == right);
    }

    [Fact]
    public async Task a_multi_stream_slice_writes_each_raised_event_only_to_the_stream_it_was_raised_to_with_string_keys()
    {
        var a = new AEvent();
        var b = new BEvent();
        var c = new CEvent();

        var slice = new EventSlice<User, string>("slice", "foo", [eventOnStream("elsewhere", 4)]);

        await applyAsync(slice, AggregationScope.MultiStream, StreamIdentity.AsString, s =>
        {
            s.AppendEvent("left", a);
            s.AppendEvent("right", b);
            s.AppendEvent("left", c);
        });

        theBatch.QuickAppends.Count.ShouldBe(2);

        var leftAction = theBatch.QuickAppends.Single(x => x.Key == "left");
        leftAction.Events.Select(x => x.Data).ShouldBe([a, c]);
        leftAction.Events.ShouldAllBe(x => x.StreamKey == "left");

        var rightAction = theBatch.QuickAppends.Single(x => x.Key == "right");
        rightAction.Events.Select(x => x.Data).ShouldBe([b]);
        rightAction.Events.ShouldAllBe(x => x.StreamKey == "right");
    }

    [Fact]
    public async Task a_new_single_stream_slice_numbers_only_its_own_stream()
    {
        // The shape the Marten docs recommend: one event back onto this stream, one onto another
        var own = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = new AEvent();
        var theirs = new BEvent();

        var slice = new EventSlice<User, Guid>(own, "foo", [eventOnStream(own, 1), eventOnStream(own, 2)]);

        await applyAsync(slice, AggregationScope.SingleStream, StreamIdentity.AsGuid, s =>
        {
            s.AppendEvent(mine);
            s.AppendEvent(other, theirs);
        });

        var versioned = theBatch.VersionedAppends.ShouldHaveSingleItem();
        versioned.Action.Id.ShouldBe(own);
        versioned.Event.Data.ShouldBe(mine);
        versioned.Version.ShouldBe(3);

        var updated = theBatch.VersionUpdates.ShouldHaveSingleItem();
        updated.Id.ShouldBe(own);
        updated.Version.ShouldBe(3);
        updated.ExpectedVersionOnServer.ShouldBe(2);

        // Nothing on this slice says what version the other stream is at, so the store has to work it out
        var appended = theBatch.QuickAppends.ShouldHaveSingleItem();
        appended.Id.ShouldBe(other);
        appended.ExpectedVersionOnServer.ShouldBeNull();
        appended.Events.Select(x => x.Data).ShouldBe([theirs]);
    }

    [Fact]
    public async Task a_new_single_stream_slice_numbers_only_its_own_stream_with_string_keys()
    {
        var mine = new AEvent();
        var theirs = new BEvent();

        var slice = new EventSlice<User, string>("own", "foo", [eventOnStream("own", 1), eventOnStream("own", 2)]);

        await applyAsync(slice, AggregationScope.SingleStream, StreamIdentity.AsString, s =>
        {
            s.AppendEvent(mine);
            s.AppendEvent("other", theirs);
        });

        var versioned = theBatch.VersionedAppends.ShouldHaveSingleItem();
        versioned.Action.Key.ShouldBe("own");
        versioned.Event.Data.ShouldBe(mine);
        versioned.Version.ShouldBe(3);

        var updated = theBatch.VersionUpdates.ShouldHaveSingleItem();
        updated.Key.ShouldBe("own");
        updated.Version.ShouldBe(3);
        updated.ExpectedVersionOnServer.ShouldBe(2);

        var appended = theBatch.QuickAppends.ShouldHaveSingleItem();
        appended.Key.ShouldBe("other");
        appended.ExpectedVersionOnServer.ShouldBeNull();
        appended.Events.Select(x => x.Data).ShouldBe([theirs]);
    }

    [Fact]
    public async Task a_new_single_stream_slice_raising_only_to_another_stream_does_not_assume_its_version()
    {
        var own = Guid.NewGuid();
        var other = Guid.NewGuid();

        var slice = new EventSlice<User, Guid>(own, "foo", [eventOnStream(own, 1), eventOnStream(own, 2)]);

        await applyAsync(slice, AggregationScope.SingleStream, StreamIdentity.AsGuid,
            s => s.AppendEvent(other, new BEvent()));

        theBatch.VersionedAppends.ShouldBeEmpty();
        theBatch.VersionUpdates.ShouldBeEmpty();

        var appended = theBatch.QuickAppends.ShouldHaveSingleItem();
        appended.Id.ShouldBe(other);
        appended.ExpectedVersionOnServer.ShouldBeNull();
    }

    [Fact]
    public async Task a_new_single_stream_slice_raising_to_itself_continues_its_version_sequence()
    {
        var own = Guid.NewGuid();
        var first = new AEvent();
        var second = new BEvent();

        var slice = new EventSlice<User, Guid>(own, "foo", [eventOnStream(own, 1), eventOnStream(own, 2)]);

        await applyAsync(slice, AggregationScope.SingleStream, StreamIdentity.AsGuid, s =>
        {
            s.AppendEvent(first);
            s.AppendEvent(second);
        });

        theBatch.VersionedAppends.Select(x => (x.Event.Data, x.Version)).ShouldBe([(first, 3L), (second, 4L)]);
        theBatch.QuickAppends.ShouldBeEmpty();

        var updated = theBatch.VersionUpdates.ShouldHaveSingleItem();
        updated.Version.ShouldBe(4);
        updated.ExpectedVersionOnServer.ShouldBe(2);
    }

    [Fact]
    public async Task a_single_stream_slice_appending_to_an_existing_stream_leaves_the_version_to_the_store()
    {
        var own = Guid.NewGuid();

        var slice = new EventSlice<User, Guid>(own, "foo", [eventOnStream(own, 5)]);

        await applyAsync(slice, AggregationScope.SingleStream, StreamIdentity.AsGuid,
            s => s.AppendEvent(new AEvent()));

        theBatch.VersionedAppends.ShouldBeEmpty();
        theBatch.VersionUpdates.ShouldBeEmpty();
        theBatch.QuickAppends.ShouldHaveSingleItem().Id.ShouldBe(own);
    }

    private class RecordingBatch : IProjectionBatch
    {
        public List<StreamAction> QuickAppends { get; } = new();

        // The version is read as it arrives, since the event instance is free to change afterwards
        public List<(StreamAction Action, IEvent Event, long Version)> VersionedAppends { get; } = new();

        public List<StreamAction> VersionUpdates { get; } = new();

        public void QuickAppendEventWithVersion(StreamAction action, IEvent @event)
            => VersionedAppends.Add((action, @event, @event.Version));

        public void UpdateStreamVersion(StreamAction action) => VersionUpdates.Add(action);

        public void QuickAppendEvents(StreamAction action) => QuickAppends.Add(action);

        public Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;

        public Task PublishMessageAsync(object message, string tenantId) => Task.CompletedTask;

        public ValueTask RecordProgress(EventRange range) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
