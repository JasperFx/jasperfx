using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests;

// jasperfx#751: the six event exception types that were declared separately per store (Marten,
// Polecat, Fisher), lifted beside EventStreamUnexpectedMaxEventIdException / EmptyEventStreamException
// / DcbConcurrencyException. The messages pinned here are the canonical ones the store copies had
// converged on; a store whose copy diverged keeps its wording through the protected
// message-overriding constructors, which the subclass tests below exercise.
public class LiftedStoreExceptionTests
{
    [Fact]
    public void unknown_event_type_carries_the_alias_and_the_registration_hint()
    {
        var ex = new UnknownEventTypeException("trip_started");

        ex.EventTypeName.ShouldBe("trip_started");
        ex.Message.ShouldBe(
            "Unknown event type name alias 'trip_started'. You may need to register this event type through StoreOptions.Events.AddEventType(type)");

        // The single-argument ctor is the "no row in hand" spelling — the sentinel the stores'
        // event read paths already used for "the sequence could not be determined".
        ex.Sequence.ShouldBe(UnknownEventTypeException.UnknownSequence);
        ex.Sequence.ShouldBe(-1);
    }

    [Fact]
    public void unknown_event_type_is_an_event_failure_context()
    {
        // marten#5048 / jasperfx#565: a shard paused by an unregistered event type names the event
        // that stopped it, and the category is deliberately distinct from EventSerialization —
        // a deployment fix, not a data fix.
        IEventFailureContext context = new UnknownEventTypeException("trip_started", 42);

        context.Category.ShouldBe(ShardFailureCategory.UnknownEventType);
        context.Sequence.ShouldBe(42);
        context.EventTypeName.ShouldBe("trip_started");

        // The type never resolved, so no event was materialized to read these from.
        context.EventId.ShouldBeNull();
        context.StreamId.ShouldBeNull();
        context.StreamKey.ShouldBeNull();
        context.TenantId.ShouldBeNull();
        context.Version.ShouldBeNull();
    }

    [Fact]
    public void event_deserialization_failure_carries_sequence_alias_and_inner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new EventDeserializationFailureException(42, "trip_started", inner);

        ex.Message.ShouldBe("Event deserialization error on sequence = 42 for event type trip_started");
        ex.Sequence.ShouldBe(42);
        ex.EventTypeName.ShouldBe("trip_started");
        ex.InnerException.ShouldBeSameAs(inner);

        IEventFailureContext context = ex;
        context.Category.ShouldBe(ShardFailureCategory.EventSerialization);
        context.EventId.ShouldBeNull();
        context.StreamId.ShouldBeNull();
        context.StreamKey.ShouldBeNull();
        context.TenantId.ShouldBeNull();
        context.Version.ShouldBeNull();
    }

    [Fact]
    public void event_deserialization_failure_accepts_an_event_type()
    {
        // Marten's ctor shape: hand over the IEventType and let the exception take the alias.
        var eventType = new FakeEventType { EventTypeName = "trip_started" };

        var ex = new EventDeserializationFailureException(7, eventType, new Exception("boom"));

        ex.EventTypeName.ShouldBe(eventType.EventTypeName);
        ex.Message.ShouldContain(eventType.EventTypeName);
    }

    [Fact]
    public void event_deserialization_failure_builds_a_correlatable_dead_letter()
    {
        // Lifted from Marten's internal ToDeadLetterEvent: the id is pre-assigned (version 7) so the
        // creating process can correlate the ShardFailure it reports with the background, retried
        // dead letter write.
        var ex = new EventDeserializationFailureException(42, "trip_started", new Exception("boom"));

        var deadLetter = ex.ToDeadLetterEvent(new ShardName("Trip", "All", 2));

        deadLetter.Id.ShouldNotBe(Guid.Empty);
        deadLetter.Id.Version.ShouldBe(7);
        deadLetter.EventSequence.ShouldBe(42);
        deadLetter.ExceptionMessage.ShouldBe(ex.Message);
        deadLetter.ExceptionType.ShouldBe("JasperFx.Events.EventDeserializationFailureException");
        deadLetter.ProjectionName.ShouldBe("Trip");
        deadLetter.ShardName.ShouldBe("All");
    }

    [Fact]
    public void non_existent_stream_names_the_id()
    {
        var id = Guid.NewGuid();
        var ex = new NonExistentStreamException(id);

        ex.Id.ShouldBe(id);
        ex.Message.ShouldBe($"Attempt to append to a nonexistent event stream '{id}'");
    }

    [Fact]
    public void existing_stream_id_collision_names_the_id()
    {
        var ex = new ExistingStreamIdCollisionException("stream-1");

        ex.Id.ShouldBe("stream-1");
        ex.AggregateType.ShouldBeNull();
        ex.Message.ShouldBe("Stream with id 'stream-1' already exists.");
    }

    [Fact]
    public void existing_stream_id_collision_can_carry_the_aggregate_type()
    {
        // Marten's shape: the aggregate type the stream was being started for.
        var ex = new ExistingStreamIdCollisionException("stream-1", typeof(AEvent));

        ex.AggregateType.ShouldBe(typeof(AEvent));
    }

    [Fact]
    public void stream_locked_carries_the_stream_id_and_optional_inner()
    {
        var inner = new TimeoutException("lock wait timeout");
        var ex = new StreamLockedException(13, inner);

        ex.StreamId.ShouldBe(13);
        ex.InnerException.ShouldBeSameAs(inner);
        ex.Message.ShouldBe("Stream '13' may be locked for updates");

        // Polecat passes a nullable inner; that stays expressible.
        new StreamLockedException(13, null).InnerException.ShouldBeNull();
    }

    [Fact]
    public void default_tenant_usage_disabled_has_the_standard_message()
    {
        new DefaultTenantUsageDisabledException().Message.ShouldBe(
            "Default tenant *DEFAULT* usage is disabled. Ensure to create a session by explicitly passing a non-default tenant in the method arg or SessionOptions.");
    }

    [Fact]
    public void default_tenant_usage_disabled_appends_to_the_prefix()
    {
        // Both store copies APPEND the caller's text to the standard prefix rather than replacing it.
        new DefaultTenantUsageDisabledException("Use a tenant-specific daemon instead.").Message.ShouldBe(
            "Default tenant *DEFAULT* usage is disabled. Use a tenant-specific daemon instead.");
    }

    [Fact]
    public void a_store_subclass_can_keep_its_diverged_message()
    {
        // The protected message-overriding ctors exist so a store whose copy diverged (Marten's
        // collision message, Fisher's unknown-event-type message) can subclass without breaking the
        // tests that pin its wording.
        var collision = new MartenishCollisionException(5, typeof(AEvent));
        collision.Message.ShouldBe("Stream #5 already exists in the database");
        collision.Id.ShouldBe(5);
        collision.AggregateType.ShouldBe(typeof(AEvent));

        var unknown = new FisherishUnknownEventTypeException("trip_started", 42);
        unknown.Message.ShouldContain("at sequence 42");
        unknown.EventTypeName.ShouldBe("trip_started");
        unknown.Sequence.ShouldBe(42);
        ((IEventFailureContext)unknown).Category.ShouldBe(ShardFailureCategory.UnknownEventType);
    }

    private class MartenishCollisionException : ExistingStreamIdCollisionException
    {
        public MartenishCollisionException(object id, Type aggregateType)
            : base($"Stream #{id} already exists in the database", id, aggregateType)
        {
        }
    }

    private class FisherishUnknownEventTypeException : UnknownEventTypeException
    {
        public FisherishUnknownEventTypeException(string? eventTypeName, long sequence)
            : base($"Unknown event type '{eventTypeName}' at sequence {sequence}.", eventTypeName, sequence)
        {
        }
    }

    private class FakeEventType : IEventType
    {
        public Type EventType => typeof(AEvent);
        public string DotNetTypeName { get; set; } = typeof(AEvent).FullName!;
        public string EventTypeName { get; set; } = "a_event";
        public string Alias => EventTypeName;

        public IEvent Wrap(object eventData)
        {
            throw new NotSupportedException();
        }
    }
}
