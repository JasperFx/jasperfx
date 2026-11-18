using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests;

// jasperfx#1215: named rewrite transforms on EventRegistry, and a rewrite run built from the stored marks
public class EventRewriteTests
{
    public record Noted(string Text);

    public record Renamed(string Text) : Noted(Text);

    public record Other(string Text);

    private static EventRegistry registryWithBang()
    {
        var registry = new EventRegistry();
        registry.AddRewriteTransform<Noted>("bang", x => x with { Text = x.Text + "!" });
        return registry;
    }

    [Fact]
    public void a_registered_transform_is_found_by_name()
    {
        var registry = registryWithBang();

        registry.TryFindRewriteTransform("bang", out var transform).ShouldBeTrue();
        transform!.EventType.ShouldBe(typeof(Noted));
        transform.Apply(new Noted("a")).ShouldBe(new Noted("a!"));
        registry.RewriteTransforms().Select(x => x.Name).ShouldBe(new[] { "bang" });
    }

    [Fact]
    public void a_transform_applies_to_types_that_can_be_cast_to_its_type()
    {
        registryWithBang().TryFindRewriteTransform("bang", out var transform);

        transform!.AppliesTo(new Renamed("a")).ShouldBeTrue();
        transform.AppliesTo(new Other("a")).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => transform.Apply(new Other("a")));
    }

    [Fact]
    public void a_second_transform_with_the_same_name_is_refused()
    {
        var registry = registryWithBang();

        Should.Throw<InvalidOperationException>(() =>
            registry.AddRewriteTransform<Other>("bang", x => x));
    }

    [Fact]
    public void a_definition_gets_its_transforms_in_its_own_order()
    {
        var registry = registryWithBang();
        registry.AddRewriteTransform<Noted>("upper", x => x with { Text = x.Text.ToUpperInvariant() });

        var transforms = registry.FindRewriteTransforms(new EventRewriteDefinition
        {
            Name = "r", Transforms = ["upper", "bang"]
        });

        transforms.Select(x => x.Name).ShouldBe(new[] { "upper", "bang" });
    }

    [Fact]
    public void an_unknown_name_refuses_the_whole_definition_and_names_every_unknown_transform()
    {
        var registry = registryWithBang();

        var refused = Should.Throw<UnknownRewriteTransformException>(() =>
            registry.FindRewriteTransforms(new EventRewriteDefinition
            {
                Name = "encrypt", Transforms = ["bang", "missing", "gone", "missing"]
            }));

        refused.RewriteName.ShouldBe("encrypt");
        refused.UnknownNames.ShouldBe(new[] { "missing", "gone" });
        refused.Message.ShouldContain("missing, gone");
    }

    [Fact]
    public void a_definition_is_plain_data_that_survives_json()
    {
        var definition = new EventRewriteDefinition
        {
            Name = "encrypt",
            EventTypes = ["order_placed"],
            Transforms = ["bang"],
            OnlyEventsStoredAsJson = true,
            BatchSize = 100,
            PauseBetweenBatches = TimeSpan.FromMilliseconds(250),
            DryRun = true,
            Tenants = ["a", "b"]
        };

        var copy = System.Text.Json.JsonSerializer.Deserialize<EventRewriteDefinition>(
            System.Text.Json.JsonSerializer.Serialize(definition))!;

        copy.Name.ShouldBe("encrypt");
        copy.EventTypes.ShouldBe(new[] { "order_placed" });
        copy.Transforms.ShouldBe(new[] { "bang" });
        copy.OnlyEventsStoredAsJson.ShouldBeTrue();
        copy.BatchSize.ShouldBe(100);
        copy.PauseBetweenBatches.ShouldBe(TimeSpan.FromMilliseconds(250));
        copy.DryRun.ShouldBeTrue();
        copy.Tenants.ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public async Task a_run_from_the_marks_captures_the_head_once_and_resumes_after_the_cursor()
    {
        var database = Substitute.For<IEventDatabase>();
        database.FetchEventRangeMarkAsync("rewrite:encrypt", null, Arg.Any<CancellationToken>()).Returns((long?)null);
        database.FetchEventRangeMarkAsync("rewrite:encrypt:cursor", null, Arg.Any<CancellationToken>()).Returns(30L);
        database.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(900L);
        database.TrySetEventRangeMarkAsync("rewrite:encrypt", null, 900, Arg.Any<CancellationToken>()).Returns(850L);

        var run = await EventRewriteRun.FromMarksAsync(database, new EventRewriteDefinition { Name = "encrypt" },
            CancellationToken.None);

        // 850 won the race to store the ceiling, so 850 is what this run stops after
        run.Cursors.ShouldHaveSingleItem().ShouldBe(new EventRangeCursor(null, 30, 850));
        run.CursorKey.ShouldBe("rewrite:encrypt:cursor");
    }

    [Fact]
    public async Task a_run_for_tenants_falls_back_to_the_store_wide_head_when_sequences_are_not_per_tenant()
    {
        var database = Substitute.For<IEventDatabase>();
        database.FetchEventRangeMarkAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((long?)null);
        database.FetchHighestEventSequenceNumber("a", Arg.Any<CancellationToken>())
            .Throws(new NotSupportedException());
        database.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(900L);
        database.TrySetEventRangeMarkAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long>(),
            Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<long>(2));

        var run = await EventRewriteRun.FromMarksAsync(database,
            new EventRewriteDefinition { Name = "encrypt", Tenants = ["a"] }, CancellationToken.None);

        run.Cursors.ShouldHaveSingleItem().ShouldBe(new EventRangeCursor("a", 0, 900));
    }

    [Fact]
    public void a_cursor_at_its_ceiling_is_complete()
    {
        new EventRangeCursor(null, 900, 900).IsComplete.ShouldBeTrue();
        new EventRangeCursor(null, 899, 900).IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void has_job_host_is_off_until_a_job_host_sets_it()
    {
        var registry = new EventRegistry();
        registry.HasJobHost.ShouldBeFalse();

        registry.HasJobHost = true;
        registry.HasJobHost.ShouldBeTrue();
    }

    [Fact]
    public async Task an_event_store_that_predates_the_seams_refuses_rewrites_and_backfills()
    {
        var database = Substitute.For<IEventDatabase>();

        IEventStore bare = new BareEventStore();
        bare.SupportsEventRewrites.ShouldBeFalse();
        bare.SupportsBackfills.ShouldBeFalse();

        await Should.ThrowAsync<NotSupportedException>(() => bare.RewriteStoredEventsAsync(
            new EventRewriteRun(database, new EventRewriteDefinition { Name = "r" }, []), CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(() => bare.RunBackfillAsync(
            new BackfillRun(database, "Trips", "year", []), CancellationToken.None));
    }

    // Overrides none of the jasperfx#1215 / #1222 members, so the defaults answer
    private sealed class BareEventStore : IEventStore
    {
        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task an_event_database_that_predates_the_seam_refuses_marks()
    {
        IEventDatabase database = Substitute.ForPartsOf<MarklessDatabase>();

        await Should.ThrowAsync<NotSupportedException>(() => database.FetchEventRangeMarkAsync("k", null));
        await Should.ThrowAsync<NotSupportedException>(() => database.TrySetEventRangeMarkAsync("k", null, 1));
        await Should.ThrowAsync<NotSupportedException>(() => database.SetEventRangeMarkAsync("k", null, 1));
    }

    public abstract class MarklessDatabase : IEventDatabase
    {
        public abstract string Identifier { get; }
        public abstract Uri DatabaseUri { get; }
        public abstract JasperFx.Events.Daemon.ShardStateTracker Tracker { get; }
        public abstract Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token);
        public abstract Task EnsureStorageExistsAsync(Type storageType, CancellationToken token);
        public abstract Task<long> ProjectionProgressFor(JasperFx.Events.Projections.ShardName name, CancellationToken token = default);
        public abstract Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token);
        public abstract string StorageIdentifier { get; }
        public abstract Task<long> FetchHighestEventSequenceNumber(CancellationToken token);
        public abstract Task<IReadOnlyList<JasperFx.Events.Projections.ShardState>> AllProjectionProgress(CancellationToken token = default);
    }
}
