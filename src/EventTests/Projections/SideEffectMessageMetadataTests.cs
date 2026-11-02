using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1101: what a message published with slice.PublishMessage(message) carries, decided from the
// slice's events. The async path through the runner is in AggregationRunnerRaisedEventsTests, which
// already has the harness for it.
public class SideEffectMessageMetadataTests
{
    private readonly RecordingBatch theBatch = new();

    private static IEvent eventWith(string? correlationId, string? causationId = null, string tenantId = "foo")
        => new Event<AEvent>(new AEvent())
        {
            StreamId = Guid.NewGuid(),
            TenantId = tenantId,
            CorrelationId = correlationId,
            CausationId = causationId
        };

    private async Task publishFromSliceWith(params IEvent[] events)
    {
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", events);
        slice.As<IEventSlice<User>>().PublishMessage(new ShipOrder("first"));

        await slice.PublishMessagesAsync(theBatch);
    }

    [Fact]
    public async Task events_that_agree_give_the_message_their_correlation()
    {
        await publishFromSliceWith(eventWith("flow-A"), eventWith("flow-A"));

        theBatch.PublishedWithTenantOnly.ShouldBeEmpty();

        var metadata = theBatch.PublishedWithMetadata.ShouldHaveSingleItem().Metadata;
        metadata.CorrelationId.ShouldBe("flow-A");
        metadata.TenantId.ShouldBe("foo");
    }

    // Under the daemon these two land on one id for the page, which is the problem, but picking either
    // one would be a guess and attribute one flow's message to the other
    [Fact]
    public async Task events_that_disagree_leave_the_message_to_the_tenant_alone()
    {
        await publishFromSliceWith(eventWith("flow-A"), eventWith("flow-B"));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task a_null_beside_a_value_is_not_agreement()
    {
        await publishFromSliceWith(eventWith("flow-A"), eventWith(null));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task a_value_beside_a_null_is_not_agreement_either()
    {
        await publishFromSliceWith(eventWith(null), eventWith("flow-A"));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task a_slice_with_no_events_leaves_the_message_to_the_tenant_alone()
    {
        await publishFromSliceWith();

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task events_with_no_metadata_at_all_leave_the_message_to_the_tenant_alone()
    {
        await publishFromSliceWith(eventWith(null), eventWith(null));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    // A deliberate decision, pinned so it stays one. Wolverine lets a causation header win over the
    // conversation id when it sets up the handler's session, so copying the event's causation here would
    // have the side effect's handler name the root message as the cause of what it appends, not itself.
    [Fact]
    public async Task causation_is_left_out_even_when_every_event_agrees_on_it()
    {
        var agreed = eventWith("flow-A", "message-1");

        await publishFromSliceWith(agreed, eventWith("flow-A", "message-1"));

        theBatch.PublishedWithMetadata.ShouldHaveSingleItem().Metadata.CausationId.ShouldBeNull();
        MessageMetadata.From(agreed).CausationId.ShouldBeNull();

        // And causation alone is not enough for a metadata publish
        await publishFromSliceWith(eventWith(null, "message-1"), eventWith(null, "message-1"));

        theBatch.PublishedWithMetadata.ShouldHaveSingleItem();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task a_stand_in_among_events_that_agree_does_not_cost_the_correlation(string standIn)
    {
        await publishFromSliceWith(eventWith("flow-A"), standInEvent(standIn), eventWith("flow-A"));

        theBatch.PublishedWithTenantOnly.ShouldBeEmpty();
        theBatch.PublishedWithMetadata.ShouldHaveSingleItem().Metadata.CorrelationId.ShouldBe("flow-A");
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task a_slice_holding_only_stand_ins_leaves_the_message_to_the_tenant_alone(string standIn)
    {
        await publishFromSliceWith(standInEvent(standIn), standInEvent(standIn));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    // Skipping a stand in must not read as skipping every null: this one is an appended event
    [Fact]
    public async Task a_null_on_an_appended_event_still_means_no_agreement_beside_a_stand_in()
    {
        await publishFromSliceWith(eventWith("flow-A"), standInEvent("reference"), eventWith(null));

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task every_plain_message_on_the_slice_carries_the_events_metadata()
    {
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", [eventWith("flow-A")]);
        slice.As<IEventSlice<User>>().PublishMessage(new ShipOrder("first"));
        slice.As<IEventSlice<User>>().PublishMessage(new ShipOrder("second"));

        await slice.PublishMessagesAsync(theBatch);

        theBatch.PublishedWithMetadata.Select(x => x.Message)
            .ShouldBe([new ShipOrder("first"), new ShipOrder("second")]);
        theBatch.PublishedWithMetadata.ShouldAllBe(x => x.Metadata.CorrelationId == "flow-A");
    }

    [Fact]
    public async Task inline_single_stream_publishes_with_the_metadata_of_each_streams_events()
    {
        var sink = new RecordingSink();
        var operations = operationsWith<MyAggregate, Guid>(sink);

        var flowA = StreamAction.Start(Guid.NewGuid(), [eventWith("flow-A")]);
        flowA.TenantId = "foo";
        var flowB = StreamAction.Start(Guid.NewGuid(), [eventWith("flow-B")]);
        flowB.TenantId = "foo";

        await new ShippingSingleStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [flowA, flowB], CancellationToken.None);

        sink.PublishedWithTenantOnly.ShouldBeEmpty();
        sink.PublishedWithMetadata.Select(x => x.Metadata.CorrelationId).ShouldBe(["flow-A", "flow-B"]);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("updated")]
    [InlineData("deleted")]
    public async Task inline_keeps_the_correlation_beside_a_stand_in(string standIn)
    {
        var sink = new RecordingSink();
        var operations = operationsWith<MyAggregate, Guid>(sink);

        var stream = StreamAction.Start(Guid.NewGuid(),
            [eventWith("flow-A"), standInEvent(standIn), eventWith("flow-A")]);
        stream.TenantId = "foo";

        await new ShippingSingleStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [stream], CancellationToken.None);

        sink.PublishedWithTenantOnly.ShouldBeEmpty();
        sink.PublishedWithMetadata.ShouldHaveSingleItem().Metadata.CorrelationId.ShouldBe("flow-A");
    }

    [Fact]
    public async Task inline_multi_stream_publishes_with_the_metadata_of_each_slices_events()
    {
        var sink = new RecordingSink();
        var operations = operationsWith<MyAggregate, Guid>(sink);

        var flowA = StreamAction.Start(Guid.NewGuid(), [eventForSlice(Guid.NewGuid(), "flow-A")]);
        var flowB = StreamAction.Start(Guid.NewGuid(), [eventForSlice(Guid.NewGuid(), "flow-B")]);

        await new ShippingMultiStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [flowA, flowB], CancellationToken.None);

        sink.PublishedWithTenantOnly.ShouldBeEmpty();
        sink.PublishedWithMetadata.Select(x => x.Metadata.CorrelationId)
            .ShouldBe(["flow-A", "flow-B"], ignoreOrder: true);
    }

    [Fact]
    public async Task inline_with_no_event_metadata_publishes_with_the_tenant_alone()
    {
        var sink = new RecordingSink();
        var operations = operationsWith<MyAggregate, Guid>(sink);

        var stream = StreamAction.Start(Guid.NewGuid(), [eventWith(null)]);
        stream.TenantId = "foo";

        await new ShippingSingleStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [stream], CancellationToken.None);

        sink.PublishedWithMetadata.ShouldBeEmpty();
        sink.PublishedWithTenantOnly.ShouldHaveSingleItem().TenantId.ShouldBe("foo");
    }

    [Fact]
    public async Task an_inline_projection_that_publishes_nothing_never_starts_a_sink()
    {
        // GetOrStartMessageSink() throws on a FakeOperations without one, and starting a batch for
        // nothing would have a store enlist it in the commit
        var operations = new FakeOperations
        {
            EnableSideEffectsOnInlineProjections = true,
            ProjectionStorage = Substitute.For<IProjectionStorage<MyAggregate, Guid>>()
        };

        var stream = StreamAction.Start(Guid.NewGuid(), [eventWith("flow-A")]);
        stream.TenantId = "foo";

        await new QuietSingleStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [stream], CancellationToken.None);
    }

    [Fact]
    public async Task explicit_metadata_wins_over_what_the_events_agree_on()
    {
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", [eventWith("flow-A")]);
        var explicitMetadata = new MessageMetadata("bar") { CorrelationId = "chosen", CausationId = "chosen-cause" };

        slice.As<IEventSlice<User>>().PublishMessage(new ShipOrder("explicit"), explicitMetadata);

        slice.As<IEventSlice<User>>().PublishedMessagesWithMetadata().ShouldHaveSingleItem().Metadata
            .CorrelationId.ShouldBe("chosen");

        // Only the plain list is this slice's to fill in, so an explicit message isn't published twice
        await slice.PublishMessagesAsync(theBatch);

        theBatch.PublishedWithMetadata.ShouldBeEmpty();
        theBatch.PublishedWithTenantOnly.ShouldBeEmpty();
    }

    [Fact]
    public async Task explicit_metadata_wins_through_an_inline_projection()
    {
        var sink = new RecordingSink();
        var operations = operationsWith<MyAggregate, Guid>(sink);

        var stream = StreamAction.Start(Guid.NewGuid(), [eventWith("flow-A")]);
        stream.TenantId = "foo";

        await new MixedSingleStreamProjection().As<IInlineProjection<FakeOperations>>()
            .ApplyAsync(operations, [stream], CancellationToken.None);

        // Plain before explicit, the order the two lists have always been published in
        sink.PublishedWithMetadata.Select(x => (x.Message, x.Metadata.CorrelationId)).ShouldBe([
            (new ShipOrder("plain"), "flow-A"),
            (new ShipOrder("explicit"), "chosen")
        ]);
    }

    [Fact]
    public void metadata_from_an_event_copies_its_correlation_and_tenant()
    {
        var metadata = MessageMetadata.From(eventWith("flow-A", tenantId: "bar"));

        metadata.CorrelationId.ShouldBe("flow-A");
        metadata.TenantId.ShouldBe("bar");
    }

    [Fact]
    public void metadata_from_an_event_with_no_metadata_says_nothing()
    {
        var metadata = MessageMetadata.From(eventWith(null, tenantId: "bar"));

        metadata.CorrelationIdEnabled.ShouldBeFalse();
        metadata.CausationIdEnabled.ShouldBeFalse();
        metadata.TenantId.ShouldBe("bar");
    }

    // The issue's mixed case, one line per event, which is what the slice itself won't guess at
    [Fact]
    public void a_projection_can_attribute_each_message_to_its_own_event_in_a_mixed_slice()
    {
        var slice = new EventSlice<User, Guid>(Guid.NewGuid(), "foo", [eventWith("flow-A"), eventWith("flow-B")]);

        foreach (var e in slice.Events())
        {
            slice.As<IEventSlice<User>>().PublishMessage(new ShipOrder("each"), MessageMetadata.From(e));
        }

        slice.As<IEventSlice<User>>().PublishedMessagesWithMetadata().Select(x => x.Metadata.CorrelationId)
            .ShouldBe(["flow-A", "flow-B"]);
    }

    private static FakeOperations operationsWith<TDoc, TId>(IMessageSink sink) => new()
    {
        EnableSideEffectsOnInlineProjections = true,
        MessageSink = sink,
        ProjectionStorage = Substitute.For<IProjectionStorage<TDoc, TId>>()
    };

    private static IEvent eventForSlice(Guid sliceId, string correlationId)
        => new Event<SliceEvent>(new SliceEvent(sliceId))
        {
            StreamId = Guid.NewGuid(), TenantId = "foo", CorrelationId = correlationId
        };

    // "reference" is what slice.Reference<T>() adds. The other two come from a composite's upstream stage
    private static IEvent standInEvent(string standIn)
    {
        var user = new User("Beast", "Hank McCoy");

        return standIn switch
        {
            "reference" => Event.For(new References<User>(user)),
            "updated" => new Updated<User>("foo", user, ActionType.Store).ToEvent(),
            "deleted" => new ProjectionDeleted<User, Guid>(Guid.NewGuid(), "foo").ToEvent(),
            _ => throw new ArgumentOutOfRangeException(nameof(standIn))
        };
    }

    private record ShipOrder(string Name);

    private record SliceEvent(Guid SliceId);

    private class ShippingSingleStreamProjection : SingleStreamProjection<MyAggregate, Guid>
    {
        public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot ?? new MyAggregate();

        public override ValueTask RaiseSideEffects(FakeOperations operations, IEventSlice<MyAggregate> slice)
        {
            slice.PublishMessage(new ShipOrder("plain"));
            return ValueTask.CompletedTask;
        }
    }

    private class MixedSingleStreamProjection : ShippingSingleStreamProjection
    {
        public override async ValueTask RaiseSideEffects(FakeOperations operations, IEventSlice<MyAggregate> slice)
        {
            // Queued first and published second: the lists keep their order, not the calls'
            slice.PublishMessage(new ShipOrder("explicit"), new MessageMetadata("foo") { CorrelationId = "chosen" });

            await base.RaiseSideEffects(operations, slice);
        }
    }

    private class QuietSingleStreamProjection : SingleStreamProjection<MyAggregate, Guid>
    {
        public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot ?? new MyAggregate();
    }

    private class ShippingMultiStreamProjection
        : JasperFxMultiStreamProjectionBase<MyAggregate, Guid, FakeOperations, FakeSession>
    {
        public ShippingMultiStreamProjection()
        {
            Identity<SliceEvent>(x => x.SliceId);
        }

        public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot ?? new MyAggregate();

        public override ValueTask RaiseSideEffects(FakeOperations operations, IEventSlice<MyAggregate> slice)
        {
            slice.PublishMessage(new ShipOrder("plain"));
            return ValueTask.CompletedTask;
        }
    }

    private class RecordingSink : IMessageSink
    {
        public List<(object Message, string TenantId)> PublishedWithTenantOnly { get; } = new();

        public List<(object Message, MessageMetadata Metadata)> PublishedWithMetadata { get; } = new();

        public ValueTask PublishAsync<T>(T message, string tenantId)
        {
            PublishedWithTenantOnly.Add((message!, tenantId));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
        {
            PublishedWithMetadata.Add((message!, metadata));
            return ValueTask.CompletedTask;
        }
    }

    private class RecordingBatch : IProjectionBatch
    {
        public List<(object Message, string TenantId)> PublishedWithTenantOnly { get; } = new();

        public List<(object Message, MessageMetadata Metadata)> PublishedWithMetadata { get; } = new();

        public Task PublishMessageAsync(object message, string tenantId)
        {
            PublishedWithTenantOnly.Add((message, tenantId));
            return Task.CompletedTask;
        }

        public Task PublishMessageAsync(object message, MessageMetadata metadata)
        {
            PublishedWithMetadata.Add((message, metadata));
            return Task.CompletedTask;
        }

        public void QuickAppendEventWithVersion(StreamAction action, IEvent @event) => throw new NotSupportedException();

        public void UpdateStreamVersion(StreamAction action) => throw new NotSupportedException();

        public void QuickAppendEvents(StreamAction action) => throw new NotSupportedException();

        public Task ExecuteAsync(CancellationToken token) => Task.CompletedTask;

        public ValueTask RecordProgress(EventRange range) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
