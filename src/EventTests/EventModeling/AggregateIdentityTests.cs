using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EventTests.EventModeling.Identities
{
    #region sample_aggregate_identity_test_types

    public record OrderId(Guid Value);
    public class Order;
    public class Shipment;
    public record OrderPlaced;

    public class OrderAggregates : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.Aggregate<Order>().IdentifiedBy<OrderId>();

            // Not stubbed yet: the identity by name
            model.Aggregate("Invoice").IdentifiedBy("InvoiceNumber");
        }
    }

    #endregion

    /// <summary>
    /// jasperfx#1004: an aggregate's identity type on the Event Model — observed from its single-stream
    /// projection, declared for design-first work, and a hotspot when the two disagree.
    /// </summary>
    public class AggregateIdentityTests
    {
        private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

        private static readonly JsonSerializerOptions CritterWatchWire = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private static AggregateDescriptor order(TypeDescriptor? identity, EventModelProvenance? provenance = null)
            => new(T<Order>(), AggregateKind.WriteAggregate, [T<OrderPlaced>()])
            {
                IdentityType = identity,
                Provenance = provenance,
            };

        private static EventModelDescriptor model(params AggregateDescriptor[] aggregates)
            => new("Orders", []) { Aggregates = aggregates };

        // ---- descriptor and wire ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void the_identity_type_round_trips(bool critterWatchWire)
        {
            var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
            var descriptor = model(order(T<OrderId>(), EventModelProvenance.Derived));

            var json = JsonSerializer.Serialize(descriptor, options);
            var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

            var aggregate = back.Aggregates.ShouldHaveSingleItem();
            aggregate.IdentityType.ShouldBe(T<OrderId>());
            aggregate.Provenance.ShouldBe(EventModelProvenance.Derived);

            json.ShouldContain(critterWatchWire ? "\"identityType\":{" : "\"IdentityType\":{");
        }

        [Fact]
        public void a_payload_from_before_the_identity_type_still_deserializes()
        {
            const string json = """
                {"name":"Orders","slices":[],"aggregates":[{"type":{"name":"Order","fullName":"Shop.Order","assemblyName":"Shop"},"kind":"writeAggregate","appliedEvents":[]}]}
                """;

            var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, CritterWatchWire)!;

            var aggregate = back.Aggregates.ShouldHaveSingleItem();
            aggregate.IdentityType.ShouldBeNull();
            aggregate.Provenance.ShouldBeNull();
        }

        [Fact]
        public void the_role_is_appended_so_existing_wire_values_keep_their_meaning()
        {
            ((int)EventModelRole.ViewProjection).ShouldBe(23);
            ((int)EventModelRole.AggregateIdentity).ShouldBe(24);

            // A model-level role: no slice ever claims it
            EventModelSliceDescriptor.Named("x").Claims(EventModelRole.AggregateIdentity).ShouldBeFalse();
        }

        [Fact]
        public void with_provenance_stamps_unattributed_aggregates_and_leaves_attributed_ones()
        {
            var stamped = model(order(T<OrderId>()), new AggregateDescriptor(T<Shipment>(), AggregateKind.WriteAggregate, [])
            {
                Provenance = EventModelProvenance.Observed,
            }).WithProvenance(EventModelProvenance.Derived);

            stamped.Aggregates.Select(x => x.Provenance)
                .ShouldBe([EventModelProvenance.Derived, EventModelProvenance.Observed]);
        }

        // ---- merge and disagreement ----

        [Fact]
        public void declared_and_observed_that_agree_merge_silently()
        {
            var merged = EventModelDescriptor.Merge("Orders", [
                model(order(T<OrderId>(), EventModelProvenance.Declared)),
                model(order(T<OrderId>(), EventModelProvenance.Derived)),
            ]);

            merged.Aggregates.ShouldHaveSingleItem().IdentityType.ShouldBe(T<OrderId>());
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_declared_identity_by_name_matches_the_real_type()
        {
            var merged = EventModelDescriptor.Merge("Orders", [
                model(order(new TypeDescriptor("OrderId", "OrderId", ""), EventModelProvenance.Declared)),
                model(order(T<OrderId>(), EventModelProvenance.Derived)),
            ]);

            merged.Aggregates.ShouldHaveSingleItem().IdentityType.ShouldBe(T<OrderId>());
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void the_derived_identity_wins_and_the_declared_one_becomes_a_hotspot()
        {
            // Registration order does not matter: the higher rung wins either way
            foreach (var order in new[] { true, false })
            {
                var declared = model(this_order(T<OrderId>(), EventModelProvenance.Declared));
                var derived = model(this_order(T<Guid>(), EventModelProvenance.Derived));

                var merged = EventModelDescriptor.Merge("Orders", order ? [declared, derived] : [derived, declared]);

                var aggregate = merged.Aggregates.ShouldHaveSingleItem();
                aggregate.IdentityType.ShouldBe(T<Guid>());
                aggregate.Provenance.ShouldBe(EventModelProvenance.Derived);

                var hotspot = merged.Hotspots.ShouldHaveSingleItem();
                hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
                hotspot.Role.ShouldBe(EventModelRole.AggregateIdentity);
                hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "Guid"));
                hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "OrderId"));
                hotspot.Text.ShouldBe("AggregateIdentity of Order: Derived claims Guid; Declared claims OrderId");
            }

            static AggregateDescriptor this_order(TypeDescriptor identity, EventModelProvenance provenance)
                => order(identity, provenance);
        }

        [Fact]
        public void an_identity_only_one_source_claims_is_kept_without_a_hotspot()
        {
            var merged = EventModelDescriptor.Merge("Orders", [
                model(order(T<OrderId>(), EventModelProvenance.Declared)),
                model(order(null, EventModelProvenance.Derived)),
            ]);

            merged.Aggregates.ShouldHaveSingleItem().IdentityType.ShouldBe(T<OrderId>());
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_real_aggregate_replacing_a_declared_one_keeps_the_declared_identity()
        {
            var declared = new AggregateDescriptor(new TypeDescriptor("Order", "Order", ""), AggregateKind.WriteAggregate, [])
            {
                IdentityType = T<OrderId>(),
                Provenance = EventModelProvenance.Declared,
            };

            var merged = EventModelDescriptor.Merge("Orders", [model(declared), model(order(null, EventModelProvenance.Derived))]);

            var aggregate = merged.Aggregates.ShouldHaveSingleItem();
            aggregate.Type.ShouldBe(T<Order>());
            aggregate.AppliedEvents.ShouldBe([T<OrderPlaced>()]);
            aggregate.IdentityType.ShouldBe(T<OrderId>());
        }

        // ---- declared ----

        [Fact]
        public async Task the_fluent_api_declares_an_identity_per_aggregate()
        {
            IEventModelDefinitionSource source = EventModelDefinitionSource.For(new OrderAggregates());
            var descriptor = await source.TryCreateAsync(new ServiceCollection().BuildServiceProvider(), default);

            descriptor!.Aggregates.Select(x => (x.Type.Name, x.IdentityType!.Name, x.IdentityType.AssemblyName))
                .ShouldBe([
                    ("Order", "OrderId", T<OrderId>().AssemblyName),
                    ("Invoice", "InvoiceNumber", ""),
                ]);
        }

        [Fact]
        public void identified_by_applies_to_the_aggregate_declared_just_before()
        {
            var builder = new EventModelBuilder();
            builder.Aggregate<Order>();
            builder.ForAggregate<Shipment>().IdentifiedBy<Guid>();
            builder.Aggregate<Order>().IdentifiedBy<OrderId>();

            builder.Build("Orders").Aggregates.Select(x => (x.Type.Name, x.IdentityType!.Name))
                .ShouldBe([("Order", "OrderId"), ("Shipment", "Guid")]);
        }

        [Fact]
        public void identified_by_with_no_aggregate_declared_throws()
        {
            Should.Throw<InvalidOperationException>(() => new EventModelBuilder().IdentifiedBy<OrderId>())
                .Message.ShouldContain("Aggregate<T>()");
        }

        // ---- observed ----

        [Fact]
        public void a_subscription_descriptor_carries_the_projections_identity_type()
        {
            var descriptor = new SubscriptionDescriptor(new FakeAggregateProjection(typeof(OrderId)), Substitute.For<IEventStore>());

            descriptor.IdentityType.ShouldBe(T<OrderId>());
            descriptor.AggregateType.ShouldBe(T<Order>());
        }

        [Fact]
        public void a_single_stream_projection_is_an_aggregate_with_its_identity()
        {
            var subscription = new SubscriptionDescriptor(new FakeAggregateProjection(typeof(OrderId)), Substitute.For<IEventStore>());

            var aggregate = ProjectionEventModelSource.ToAggregate(subscription).ShouldNotBeNull();
            aggregate.Type.ShouldBe(T<Order>());
            aggregate.Kind.ShouldBe(AggregateKind.WriteAggregate);
            aggregate.IdentityType.ShouldBe(T<OrderId>());
            aggregate.AppliedEvents.ShouldBe([T<OrderPlaced>()]);
        }

        [Fact]
        public void other_registrations_are_not_aggregates()
        {
            ProjectionEventModelSource.ToAggregate(new SubscriptionDescriptor(SubscriptionType.MultiStreamProjection)
            {
                AggregateType = T<Order>(), IdentityType = T<OrderId>(),
            }).ShouldBeNull();

            ProjectionEventModelSource.ToAggregate(new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection))
                .ShouldBeNull();
        }

        [Fact]
        public async Task the_store_derived_identity_beats_a_declaration()
        {
            var subscription = new SubscriptionDescriptor(new FakeAggregateProjection(typeof(Guid)), Substitute.For<IEventStore>());
            var usage = new EventStoreUsage { SubjectUri = new Uri("store://orders") };
            usage.Subscriptions.Add(subscription);

            var store = Substitute.For<IEventStore>();
            store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));

            var services = new ServiceCollection()
                .AddProjectionEventModelSource(_ => [store])
                .AddEventModel("EventModel", model => model.Aggregate<Order>().IdentifiedBy<OrderId>())
                .BuildServiceProvider();

            var assembled = (await EventModelDiscovery.AssembleAsync(services, TestContext.Current.CancellationToken)).Single();

            var aggregate = assembled.Aggregates.ShouldHaveSingleItem();
            aggregate.IdentityType.ShouldBe(T<Guid>());

            var hotspot = assembled.Hotspots.ShouldHaveSingleItem();
            hotspot.Role.ShouldBe(EventModelRole.AggregateIdentity);
            hotspot.WinningClaim!.Provenance.ShouldBe(EventModelProvenance.Derived);
            hotspot.LosingClaim!.Value.ShouldBe("OrderId");
        }

        private sealed class FakeAggregateProjection(Type identityType) : ISubscriptionSource, IAggregateProjection
        {
            public string Name => nameof(Order);
            public uint Version => 1;
            public SubscriptionType Type => SubscriptionType.SingleStreamProjection;
            public ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;
            public ShardName[] ShardNames() => [];
            public Type ImplementationType => typeof(FakeAggregateProjection);
            public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);

            public Type IdentityType => identityType;
            public Type AggregateType => typeof(Order);
            public AggregationScope Scope => AggregationScope.SingleStream;
            public Type[] AllEventTypes => [typeof(OrderPlaced)];
            public AsyncOptions Options { get; } = new();
            public NaturalKeyDefinition? NaturalKeyDefinition => null;
        }
    }
}
