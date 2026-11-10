using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Shouldly;

namespace EventStoreTests.Documents
{
    using EventStoreContract;

    /// <summary>
    /// jasperfx#1052: the facts a consumer asks of the store a chain writes to, through non-generic
    /// <see cref="IEventStore" />.
    /// </summary>
    public class InMemoryEventStoreFactTests
    {
        [Theory]
        [InlineData(StreamIdentity.AsGuid)]
        [InlineData(StreamIdentity.AsString)]
        public void stream_identity_comes_from_the_registry_with_no_store_code(StreamIdentity identity)
        {
            var store = new InMemoryDocumentStore();
            store.Events.StreamIdentity = identity;

            ((IEventStore)store).StreamIdentity.ShouldBe(identity);
        }

        [Fact]
        public void identity_type_keeps_a_strong_typed_identifier_as_itself()
        {
            IEventStore store = new InMemoryDocumentStore();

            store.IdentityTypeFor(typeof(StrongTicket)).ShouldBe(typeof(TicketId));
        }

        [Fact]
        public void identity_type_is_never_nullable()
        {
            // wolverine GH-3942: Nullable<AlertId> sends the workflow looking for a member that exists nowhere
            IEventStore store = new InMemoryDocumentStore();

            store.IdentityTypeFor(typeof(NullableIdTicket)).ShouldBe(typeof(TicketId));
        }

        [Fact]
        public void a_registered_natural_key_is_found()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Snapshot<KeyedOrder>(SnapshotLifecycle.Inline);

            var key = ((IEventStore)store).FindNaturalKeyDefinition(typeof(KeyedOrder));

            key.ShouldNotBeNull().AggregateType.ShouldBe(typeof(KeyedOrder));
        }

        [Fact]
        public void an_aggregate_with_no_natural_key_is_a_null_answer_rather_than_a_refusal()
        {
            var store = new InMemoryDocumentStore();
            store.Projections.Snapshot<PlainOrder>(SnapshotLifecycle.Inline);

            ((IEventStore)store).FindNaturalKeyDefinition(typeof(PlainOrder)).ShouldBeNull();
            ((IEventStore)store).FindNaturalKeyDefinition(typeof(Ticket)).ShouldBeNull();
        }
    }

    /// <summary>
    /// jasperfx#1052: the store-agnostic subscription. A store that cannot run one has to say so at
    /// registration, because the alternative is finding out when the daemon starts.
    /// </summary>
    public class EventSubscriptionRegistrationTests
    {
        private class Subscription : IEventSubscription
        {
            public string SubscriptionName => "orders";
            public uint SubscriptionVersion => 3;
            public AsyncOptions Options { get; } = new() { BatchSize = 42 };

            public void Filter(IEventFilterable filterable) => filterable.IncludeType<TicketOpened>();

            public Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller,
                IDocumentWriteOperations operations, CancellationToken cancellationToken)
                => Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }

        private class StoreBase : JasperFxSubscriptionBase<IInMemoryDocumentSession, IInMemoryQuerySession, Subscription>
        {
            public StoreBase(Subscription subscription) : base(subscription) => ConfigureFrom(subscription);
        }

        [Fact]
        public void a_store_that_cannot_run_a_subscription_refuses_it_at_registration()
        {
            IEventSubscriptionRegistrar projections = new InMemoryDocumentStore().Projections;

            var refused = Should.Throw<NotSupportedException>(() => projections.Subscribe(new Subscription()));

            refused.Message.ShouldContain("InMemoryProjectionGraph");
            refused.Message.ShouldContain("orders");
        }

        [Fact]
        public void a_store_wrapping_a_subscription_takes_its_configuration_from_it()
        {
            var subscription = new Subscription();

            var wrapped = new StoreBase(subscription);

            wrapped.Name.ShouldBe("orders");
            wrapped.Version.ShouldBe(3u);
            wrapped.Options.ShouldBeSameAs(subscription.Options);
            wrapped.IncludedEventTypes.ShouldContain(typeof(TicketOpened));
        }
    }
}

namespace EventStoreTests.Documents.EventStoreContract
{
    public class Ticket
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    public readonly record struct TicketId(Guid Value);

    public class StrongTicket
    {
        public TicketId Id { get; set; }
    }

    public class NullableIdTicket
    {
        public TicketId? Id { get; set; }
    }

    public record TicketOpened(string Title);

    public record OrderPlaced(string Number);

    public partial class KeyedOrder
    {
        public Guid Id { get; set; }

        [NaturalKey]
        public string Number { get; set; } = "";

        [NaturalKeySource]
        public static KeyedOrder Create(OrderPlaced e) => new() { Number = e.Number };
    }

    public partial class PlainOrder
    {
        public Guid Id { get; set; }

        public static PlainOrder Create(OrderPlaced e) => new();
    }
}
