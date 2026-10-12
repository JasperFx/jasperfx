using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling.Queries
{
    #region sample_query_slice_test_types

    public record AccountOpened;
    public record FundsDeposited;
    public record AccountSummary(Guid Id);
    public record GetAccountSummary(Guid Id);
    public record AuditRecorded;
    public record NotifyCompliance;

    public class AccountQueries : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            // The projection: events -> read model
            model.View<AccountSummary>()
                .From<AccountOpened>()
                .From<FundsDeposited>();

            // The endpoint that reads it: trigger -> read -> response. Named the way
            // Wolverine.HTTP names the slice for a GET endpoint, so the two merge.
            model.Query<AccountSummary>("GET /api/accounts/{id}")
                .TriggeredBy(TriggerKind.Http);

            // A query message handled by a Wolverine handler, named for the message
            model.Query("GetAccountSummary")
                .TriggeredBy(TriggerKind.MessageHandler)
                .Command<GetAccountSummary>()
                .Reads<AccountSummary>();
        }
    }

    #endregion

    /// <summary>
    /// jasperfx#1003: a read-only endpoint is a <see cref="SlicePattern.Query"/> slice, not a View.
    /// </summary>
    public class QuerySlicePatternTests
    {
        private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

        private static readonly JsonSerializerOptions CritterWatchWire = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private static async Task<EventModelDescriptor> declare(EventModelDefinition definition)
        {
            IEventModelDefinitionSource source = EventModelDefinitionSource.For(definition);
            var descriptor = await source.TryCreateAsync(new ServiceCollection().BuildServiceProvider(), default);
            return descriptor!.WithProvenance(source.Provenance);
        }

        private static EventModelSliceDescriptor query(string name = "GetAccountSummary") =>
            EventModelSliceDescriptor.Named(name) with
            {
                Pattern = SlicePattern.Query,
                TriggerKind = TriggerKind.Http,
                TriggerOrigin = new PublisherOrigin { HttpMethod = "GET", HttpRoute = "/api/accounts/{id}", Label = "GET /api/accounts/{id}" },
                ReadsFrom = [T<AccountSummary>()],
            };

        private static EventModelSliceDescriptor view() =>
            EventModelSliceDescriptor.Named(nameof(AccountSummary)) with
            {
                Pattern = SlicePattern.View,
                ConsumedEvents = [T<AccountOpened>()],
                ReadModelTypes = [T<AccountSummary>()],
            };

        [Fact]
        public void query_is_appended_last_so_existing_wire_values_keep_their_meaning()
        {
            ((int)SlicePattern.Command).ShouldBe(0);
            ((int)SlicePattern.View).ShouldBe(1);
            ((int)SlicePattern.Automation).ShouldBe(2);
            ((int)SlicePattern.Translation).ShouldBe(3);
            ((int)SlicePattern.Query).ShouldBe(4);
            ((int)HotspotOrigin.PatternViolation).ShouldBe(6);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void a_query_slice_round_trips(bool critterWatchWire)
        {
            var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
            var model = new EventModelDescriptor("Accounts", [view(), query()]);

            var json = JsonSerializer.Serialize(model, options);
            var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

            var slice = back.Slices.Single(x => x.Name == "GetAccountSummary");
            slice.Pattern.ShouldBe(SlicePattern.Query);
            slice.ReadsFrom.ShouldBe([T<AccountSummary>()]);
            slice.Elements.ShouldBe(query().Elements);
            back.Links.ShouldBe(model.Links);

            if (critterWatchWire)
            {
                json.ShouldContain("\"pattern\":\"query\"");
            }
            else
            {
                json.ShouldContain("\"Pattern\":4");
            }
        }

        [Fact]
        public void a_query_reads_into_its_trigger_and_carries_no_event_or_command()
        {
            var slice = query();

            var read = slice.Elements.Single(x => x.Kind == EventModelElementKind.ReadModel);
            read.Type.ShouldBe(T<AccountSummary>());
            read.Lane.ShouldBe(EventModelLane.ReadModel);

            var trigger = slice.Elements.Single(x => x.Kind == EventModelElementKind.Trigger);
            slice.Edges.ShouldBe([new EventModelEdge(read.Id, trigger.Id)]);

            slice.Elements.ShouldNotContain(x => x.Kind == EventModelElementKind.Event);
            slice.Elements.ShouldNotContain(x => x.Kind == EventModelElementKind.Command);
            slice.PatternViolations.ShouldBeEmpty();
        }

        [Fact]
        public void a_query_links_to_the_view_that_produces_the_read_model_it_reads()
        {
            var model = new EventModelDescriptor("Accounts", [view(), query()]);

            var link = model.Links.ShouldHaveSingleItem();
            link.Kind.ShouldBe(EventModelLinkKind.ReadModelRead);
            link.FromSlice.ShouldBe(nameof(AccountSummary));
            link.ToSlice.ShouldBe("GetAccountSummary");
            link.Via.ShouldBe(T<AccountSummary>());
            link.FromElementId.ShouldBe(EventModelElement.IdFor(nameof(AccountSummary), EventModelElementKind.ReadModel, T<AccountSummary>().FullName));
            link.ToElementId.ShouldBe(EventModelElement.IdFor("GetAccountSummary", EventModelElementKind.ReadModel, T<AccountSummary>().FullName));
        }

        [Fact]
        public void a_declared_query_links_to_a_declared_view_by_name()
        {
            var declaredView = view() with { ReadModelTypes = [new TypeDescriptor("AccountSummary", "AccountSummary", "")] };
            var model = new EventModelDescriptor("Accounts", [declaredView, query()]);

            model.Links.ShouldHaveSingleItem().Kind.ShouldBe(EventModelLinkKind.ReadModelRead);
        }

        [Fact]
        public void a_query_that_appends_events_is_a_hotspot()
        {
            var slice = query() with { EmittedEvents = [T<AuditRecorded>()] };

            var violation = slice.PatternViolations.ShouldHaveSingleItem();
            violation.Origin.ShouldBe(HotspotOrigin.PatternViolation);
            violation.Role.ShouldBe(EventModelRole.EmittedEvents);
            violation.Text.ShouldBe("EmittedEvents: a Query slice changes no state, but this one claims AuditRecorded");

            // ...rendered in the wireframe lane, attributed to the rung that claimed the events
            var element = slice.Elements.Single(x => x.Kind == EventModelElementKind.Hotspot);
            element.Label.ShouldBe(violation.Text);
            element.Lane.ShouldBe(EventModelLane.Wireframe);
            element.Provenance.ShouldBe(EventModelProvenance.Declared);

            // Computed, never stored: the merge's hotspot list stays the merge's
            slice.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public void a_query_that_sends_a_command_or_starts_a_stream_is_a_hotspot()
        {
            var slice = query() with
            {
                PublishedMessages = [T<NotifyCompliance>()],
                StartsStream = T<AccountSummary>(),
            };

            slice.PatternViolations.Select(x => x.Role)
                .ShouldBe([EventModelRole.StartsStream, EventModelRole.PublishedMessages]);
        }

        [Fact]
        public void a_query_that_live_aggregates_consumes_events_without_a_hotspot()
        {
            var slice = query() with { ConsumedEvents = [T<AccountOpened>()] };

            slice.PatternViolations.ShouldBeEmpty();
        }

        [Fact]
        public void a_command_slice_that_emits_events_is_not_a_violation()
        {
            var slice = EventModelSliceDescriptor.Named("Open") with
            {
                Pattern = SlicePattern.Command,
                EmittedEvents = [T<AccountOpened>()],
            };

            slice.PatternViolations.ShouldBeEmpty();
        }

        [Fact]
        public void a_violation_is_evaluated_on_the_merged_roles()
        {
            // Declared as a Query, but the code appends an event: the code wins the pattern, and
            // the difference is a SourceDisagreement rather than a Query violation.
            var declared = query() with { Provenance = EventModelProvenance.Declared };
            var derived = EventModelSliceDescriptor.Named("GetAccountSummary") with
            {
                Provenance = EventModelProvenance.Derived,
                Pattern = SlicePattern.Command,
                EmittedEvents = [T<AuditRecorded>()],
            };

            var merged = declared.Merge(derived);

            merged.Pattern.ShouldBe(SlicePattern.Command);
            merged.PatternViolations.ShouldBeEmpty();

            var disagreement = merged.Hotspots.ShouldHaveSingleItem();
            disagreement.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
            disagreement.Role.ShouldBe(EventModelRole.Pattern);
            disagreement.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "Command"));
            disagreement.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "Query"));
        }

        [Fact]
        public void a_declared_query_against_code_still_deriving_a_view_is_a_source_disagreement()
        {
            // Wolverine derives a GET endpoint as a View until it learns Query; the code wins, and the
            // model shows the gap until the sources catch up.
            var declared = query() with { Provenance = EventModelProvenance.Declared };
            var derived = EventModelSliceDescriptor.Named("GetAccountSummary") with
            {
                Provenance = EventModelProvenance.Derived,
                Pattern = SlicePattern.View,
                ReadModelTypes = [T<AccountSummary>()],
            };

            var merged = declared.Merge(derived);

            merged.Pattern.ShouldBe(SlicePattern.View);
            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Role.ShouldBe(EventModelRole.Pattern);
            hotspot.Text.ShouldBe("Pattern: Derived claims View; Declared claims Query");
        }

        [Fact]
        public void a_query_and_a_view_that_agree_on_query_merge_silently()
        {
            var declared = query() with { Provenance = EventModelProvenance.Declared };
            var derived = query() with { Provenance = EventModelProvenance.Derived };

            var merged = declared.Merge(derived);

            merged.Pattern.ShouldBe(SlicePattern.Query);
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public async Task the_fluent_api_declares_query_slices()
        {
            var model = await declare(new AccountQueries());

            var http = model.Slices.Single(x => x.Name == "GET /api/accounts/{id}");
            http.Pattern.ShouldBe(SlicePattern.Query);
            http.TriggerKind.ShouldBe(TriggerKind.Http);
            http.ReadsFrom.ShouldBe([T<AccountSummary>()]);
            http.EmittedEvents.ShouldBeEmpty();
            http.ReadModelTypes.ShouldBeEmpty();
            http.Provenance.ShouldBe(EventModelProvenance.Declared);

            var handler = model.Slices.Single(x => x.Name == "GetAccountSummary");
            handler.Pattern.ShouldBe(SlicePattern.Query);
            handler.CommandType.ShouldBe(T<GetAccountSummary>());
            handler.ReadsFrom.ShouldBe([T<AccountSummary>()]);

            // Both queries link to the view that produces what they read
            model.Links.Where(x => x.Kind == EventModelLinkKind.ReadModelRead)
                .Select(x => (x.FromSlice, x.ToSlice))
                .ShouldBe([
                    (nameof(AccountSummary), "GET /api/accounts/{id}"),
                    (nameof(AccountSummary), "GetAccountSummary"),
                ]);
        }

        [Fact]
        public void the_typed_query_verb_is_named_apart_from_the_view()
        {
            var builder = new EventModelBuilder();
            builder.View<AccountSummary>();
            builder.Query<AccountSummary>();

            var slices = builder.BuildSlices();
            slices.Select(x => x.Name).ShouldBe(["AccountSummary", "GetAccountSummary"]);
            slices[1].Pattern.ShouldBe(SlicePattern.Query);
            slices[1].ReadsFrom.ShouldBe([T<AccountSummary>()]);
        }

        [Fact]
        public void a_query_takes_no_default_aggregate()
        {
            var builder = new EventModelBuilder();
            builder.ForAggregate<AccountSummary>();
            builder.Query("GetBalance").Reads<AccountSummary>();

            var slice = builder.BuildSlices().Single();
            slice.AggregateTypes.ShouldBeEmpty();
            slice.AggregateDeclaration.ShouldBeNull();
        }
    }
}
