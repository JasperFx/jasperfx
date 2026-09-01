using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#728 — store selection and the source read, exercised against a fake
/// <see cref="IEventStore"/> so both halves are pinned without a database.
/// </summary>
public class ProjectionRunSourceTests
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    private static EventRecord evt(long version) => new(
        Guid.NewGuid(), version, version, "trip-1", $"Event{version}", Empty, null,
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), null, null);

    private sealed class FakeEventStore(string subject, params EventRecord[] events): IEventStore
    {
        public List<string> Calls { get; } = [];

        public Uri Subject { get; } = new(subject);

        public async IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, CancellationToken ct)
        {
            Calls.Add($"ReadStreamAsync({streamId})");
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }

        public async IAsyncEnumerable<EventRecord> QueryByTagsAsync(
            IReadOnlyDictionary<string, string> tags, CancellationToken ct)
        {
            Calls.Add($"QueryByTagsAsync({string.Join(",", tags.Select(x => $"{x.Key}={x.Value}"))})");
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }

        public Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();

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

    private static ProjectionRunInput forStream(string stream = "trip-1")
        => new() { ProjectionName = "Trips", StreamFlag = stream };

    [Fact]
    public void the_only_store_is_selected_without_a_flag()
    {
        var store = new FakeEventStore("marten://main");
        var (selected, error) = ProjectionRunSource.SelectStore([store], null);

        selected.ShouldBeSameAs(store);
        error.ShouldBeNull();
    }

    [Fact]
    public void no_stores_is_an_error_not_an_empty_run()
    {
        var (selected, error) = ProjectionRunSource.SelectStore([], null);

        selected.ShouldBeNull();
        error.ShouldBe("No event stores are registered in this application");
    }

    [Fact]
    public void more_than_one_store_and_no_flag_is_refused_rather_than_guessed()
    {
        // Replaying against the wrong store looks exactly like a broken projection, so a first-match
        // guess is worse than an error.
        var (selected, error) = ProjectionRunSource.SelectStore(
            [new FakeEventStore("marten://main"), new FakeEventStore("marten://reporting")], null);

        selected.ShouldBeNull();
        error.ShouldContain("specify --store");
        error.ShouldContain("marten://reporting");
    }

    [Fact]
    public void a_store_is_matched_by_subject_uri()
    {
        var reporting = new FakeEventStore("marten://reporting");
        var (selected, _) = ProjectionRunSource.SelectStore(
            [new FakeEventStore("marten://main"), reporting], "marten://reporting");

        selected.ShouldBeSameAs(reporting);
    }

    [Fact]
    public void a_store_is_matched_by_bare_scheme()
    {
        var polecat = new FakeEventStore("polecat://main");
        var (selected, _) = ProjectionRunSource.SelectStore(
            [new FakeEventStore("marten://main"), polecat], "polecat");

        selected.ShouldBeSameAs(polecat);
    }

    [Fact]
    public void an_ambiguous_store_flag_is_refused()
    {
        var (selected, error) = ProjectionRunSource.SelectStore(
            [new FakeEventStore("marten://main"), new FakeEventStore("marten://reporting")], "marten");

        selected.ShouldBeNull();
        error.ShouldContain("matches more than one event store");
    }

    [Fact]
    public void an_unmatched_store_flag_names_what_is_there()
    {
        var (selected, error) = ProjectionRunSource.SelectStore([new FakeEventStore("marten://main")], "fisher");

        selected.ShouldBeNull();
        error.ShouldContain("marten://main");
    }

    [Fact]
    public async Task a_stream_read_takes_every_event()
    {
        var store = new FakeEventStore("marten://main", evt(1), evt(2), evt(3));

        var result = await ProjectionRunSource.ReadAsync(store, forStream(), CancellationToken.None);

        result.Events.Select(x => x.StreamVersion).ShouldBe([1, 2, 3]);
        result.Truncated.ShouldBeFalse();
        store.Calls.ShouldBe(["ReadStreamAsync(trip-1)"]);
    }

    [Fact]
    public async Task a_slice_is_bounded_on_both_ends_inclusively()
    {
        var store = new FakeEventStore("marten://main", evt(1), evt(2), evt(3), evt(4), evt(5));
        var input = forStream();
        input.FromFlag = 2;
        input.ToFlag = 4;

        var result = await ProjectionRunSource.ReadAsync(store, input, CancellationToken.None);

        result.Events.Select(x => x.StreamVersion).ShouldBe([2, 3, 4]);
    }

    [Fact]
    public async Task a_tag_query_reads_by_tags()
    {
        var store = new FakeEventStore("marten://main", evt(1), evt(2));
        var input = new ProjectionRunInput { ProjectionName = "Enrollments" };
        input.TagFlag["course"] = "c-1";

        var result = await ProjectionRunSource.ReadAsync(store, input, CancellationToken.None);

        result.Events.Count.ShouldBe(2);
        store.Calls.ShouldBe(["QueryByTagsAsync(course=c-1)"]);
    }

    [Fact]
    public async Task the_event_cap_stops_the_read_and_says_so()
    {
        // A capped read produces a timeline that looks complete. The flag is the only thing that
        // stops a truncated replay from reading as the whole story.
        var store = new FakeEventStore("marten://main", evt(1), evt(2), evt(3), evt(4));
        var input = forStream();
        input.MaxEventsFlag = 2;

        var result = await ProjectionRunSource.ReadAsync(store, input, CancellationToken.None);

        result.Events.Count.ShouldBe(2);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task a_read_that_ends_exactly_on_the_cap_is_not_truncated()
    {
        var store = new FakeEventStore("marten://main", evt(1), evt(2));
        var input = forStream();
        input.MaxEventsFlag = 2;

        var result = await ProjectionRunSource.ReadAsync(store, input, CancellationToken.None);

        result.Events.Count.ShouldBe(2);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task a_null_tenant_reads_store_globally()
    {
        // The command always calls the tenant-scoped overload; a null tenant delegates to the
        // store-global member by contract (jasperfx#503), so there is only one code path here.
        var store = new FakeEventStore("marten://main", evt(1));

        await ProjectionRunSource.ReadAsync(store, forStream(), CancellationToken.None);

        store.Calls.ShouldBe(["ReadStreamAsync(trip-1)"]);
    }

    [Fact]
    public async Task a_tenant_on_a_single_tenant_store_is_refused_rather_than_ignored()
    {
        var store = new FakeEventStore("marten://main", evt(1));
        var input = forStream();
        input.TenantFlag = "tenant-1";

        await Should.ThrowAsync<NotSupportedException>(
            () => ProjectionRunSource.ReadAsync(store, input, CancellationToken.None));
    }
}
