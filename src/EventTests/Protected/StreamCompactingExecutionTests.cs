using JasperFx.Events;
using JasperFx.Events.Protected;
using NSubstitute;
using Shouldly;

namespace EventTests.Protected;

/// <summary>
/// The shared compaction (#1048). It deletes events, so these are mostly about not losing any: what a failed
/// snapshot leaves, what an archiver failing or being skipped leaves, and which tenant's rows it touches.
/// </summary>
public class StreamCompactingExecutionTests
{
    private static readonly Guid theStream = Guid.NewGuid();

    private readonly FakeEventRows theRows = new();
    private readonly TestCompacting theCompacting = new();

    private void aStream(string tenant = FakeEventRows.DefaultTenant, Guid? streamId = null)
    {
        var stream = streamId ?? theStream;

        theRows.Add(1, 1, stream, new MeterInstalled("Substation A"), tenant);
        theRows.Add(2, 2, stream, new MeterRead(10), tenant);
        theRows.Add(3, 3, stream, new MeterRead(20), tenant);
        theRows.Add(4, 4, stream, new MeterRead(30), tenant);
    }

    private static StreamCompactingRequest<Meter> aRequest(Action<StreamCompactingRequest<Meter>>? configure = null)
    {
        var request = new StreamCompactingRequest<Meter>(theStream);
        configure?.Invoke(request);

        return request;
    }

    private static Meter? foldOf(IEnumerable<EventRow> rows)
        => Meter.Fold(rows.OrderBy(x => x.Sequence).Select(x => x.Hydrate()).ToList());

    [Fact]
    public async Task replaces_the_last_event_with_the_snapshot_and_deletes_the_rest()
    {
        aStream();

        await using var session = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(), session)).ShouldBeTrue();
        await session.CommitAsync();

        var survivor = theRows.Rows.ShouldHaveSingleItem();

        survivor.Sequence.ShouldBe(4);
        survivor.Version.ShouldBe(4);

        var compacted = survivor.Body.ShouldBeOfType<Compacted<Meter>>();
        compacted.Snapshot.ShouldBe(new Meter("Substation A", 60));
        compacted.PreviousStreamId.ShouldBe(theStream);
        compacted.PreviousStreamKey.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task nothing_is_written_until_the_session_commits()
    {
        aStream();

        await using var session = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), session);

        session.Saves.ShouldBe(0);
        theRows.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task compacts_a_string_identified_stream()
    {
        theRows.Add(1, 1, Guid.Empty, new MeterInstalled("Substation A"), streamKey: "meter/a");
        theRows.Add(2, 2, Guid.Empty, new MeterRead(10), streamKey: "meter/a");

        await using var session = theRows.Session();

        var request = new StreamCompactingRequest<Meter>("meter/a");
        (await theCompacting.ExecuteAsync(request, session)).ShouldBeTrue();
        await session.CommitAsync();

        var compacted = theRows.Rows.ShouldHaveSingleItem().Body.ShouldBeOfType<Compacted<Meter>>();
        compacted.PreviousStreamId.ShouldBe(Guid.Empty);
        compacted.PreviousStreamKey.ShouldBe("meter/a");

        theCompacting.Watermarks.ShouldBe([(session, (Guid?)null, "meter/a", 2L)]);
    }

    [Fact]
    public async Task a_version_cutoff_keeps_the_events_after_it()
    {
        aStream();

        await using var session = theRows.Session();

        var request = aRequest(x => x.Version = 2);
        await theCompacting.ExecuteAsync(request, session);
        await session.CommitAsync();

        theRows.Rows.Select(x => x.Sequence).ShouldBe([2, 3, 4]);
        theRows.Row(2).Body.ShouldBeOfType<Compacted<Meter>>().Snapshot.ShouldBe(new Meter("Substation A", 10));

        theCompacting.Watermarks.ShouldBe([(session, theStream, (string?)null, 2L)]);
        foldOf(theRows.Rows).ShouldBe(new Meter("Substation A", 60));
    }

    [Fact]
    public async Task the_cutoffs_and_the_token_reach_the_read()
    {
        aStream();

        await using var session = theRows.Session();

        var timestamp = DateTimeOffset.UtcNow;
        using var cancellation = new CancellationTokenSource();

        await theCompacting.ExecuteAsync(aRequest(x =>
        {
            x.Version = 3;
            x.Timestamp = timestamp;
            x.CancellationToken = cancellation.Token;
        }), session);

        await session.Events.Received(1)
            .FetchStreamAsync(theStream, 3, timestamp, 0, cancellation.Token);
    }

    [Fact]
    public async Task stamps_the_request_with_the_point_it_compacted_to()
    {
        theRows.Add(11, 1, theStream, new MeterInstalled("Substation A"));
        theRows.Add(17, 2, theStream, new MeterRead(10));

        await using var session = theRows.Session();

        var request = aRequest();
        await theCompacting.ExecuteAsync(request, session);

        request.Version.ShouldBe(2);
        request.Sequence.ShouldBe(17);
    }

    [Fact]
    public async Task a_stream_of_one_event_has_nothing_to_delete()
    {
        theRows.Add(1, 1, theStream, new MeterInstalled("Substation A"));

        await using var session = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(), session)).ShouldBeTrue();

        session.Queued.ShouldBe(["replace", "watermark"]);
    }

    [Fact]
    public async Task compacting_a_stream_that_does_not_exist_queues_nothing()
    {
        await using var session = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(), session)).ShouldBeFalse();

        session.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_stream_already_compacted_to_this_point_is_left_alone()
    {
        aStream();

        await using (var session = theRows.Session())
        {
            await theCompacting.ExecuteAsync(aRequest(), session);
            await session.CommitAsync();
        }

        await using var second = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(), second)).ShouldBeFalse();

        second.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task compacting_again_after_more_events_folds_on_from_the_snapshot()
    {
        aStream();

        await using (var session = theRows.Session())
        {
            await theCompacting.ExecuteAsync(aRequest(), session);
            await session.CommitAsync();
        }

        theRows.Add(5, 5, theStream, new MeterRead(40));

        await using var second = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), second);
        await second.CommitAsync();

        theRows.Rows.ShouldHaveSingleItem().Body.ShouldBeOfType<Compacted<Meter>>()
            .Snapshot.ShouldBe(new Meter("Substation A", 100));
    }

    // ---- nothing to snapshot ----

    [Fact]
    public async Task a_stream_that_folds_to_nothing_loses_nothing()
    {
        // Marten and Polecat wrote new Compacted<T>(null!, ...) here and deleted every event under it
        aStream();
        theRows.Add(5, 5, theStream, new MeterRemoved());

        var archiver = new RecordingArchiver<FakeSession>();

        await using var session = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(x => x.Archiver = archiver), session)).ShouldBeFalse();

        session.Queued.ShouldBeEmpty();
        archiver.Calls.ShouldBe(0);

        await session.CommitAsync();
        theRows.Rows.Count.ShouldBe(5);
    }

    // ---- losing events to a failed snapshot ----

    [Fact]
    public async Task the_snapshot_is_queued_before_the_deletes_and_the_watermark_after_them()
    {
        aStream();

        await using var session = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), session);

        session.Queued.ShouldBe(["replace", "delete", "watermark"]);
    }

    [Fact]
    public async Task a_snapshot_the_store_refuses_outright_never_reaches_the_deletes()
    {
        aStream();

        await using var session = theRows.Session();
        session.ReplaceFailure = () => throw new NotSupportedException("A binary event body cannot be rewritten");

        await Should.ThrowAsync<NotSupportedException>(() => theCompacting.ExecuteAsync(aRequest(), session));

        session.Queued.ShouldBeEmpty();

        await session.CommitAsync();
        theRows.Rows.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("watermark")]
    public async Task a_write_failing_at_commit_rolls_back_every_other_one(string failing)
    {
        aStream();
        var before = foldOf(theRows.Rows);

        await using var session = theRows.Session();
        session.FailAtCommit = failing;

        await theCompacting.ExecuteAsync(aRequest(), session);

        await Should.ThrowAsync<InvalidOperationException>(() => session.CommitAsync());

        theRows.Rows.Select(x => x.Sequence).ShouldBe([1, 2, 3, 4]);
        theRows.Rows.ShouldAllBe(x => x.Body.GetType() != typeof(Compacted<Meter>));
        foldOf(theRows.Rows).ShouldBe(before);
        theCompacting.AppliedWatermarks.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task the_stream_reads_the_same_after_any_prefix_of_the_queue(int applied)
    {
        // Stronger than the transaction needs it to be, and what the order is for: even a batch that stopped
        // part way loses nothing. With the deletes first, stopping after one leaves the events gone and no snapshot.
        aStream();
        var before = foldOf(theRows.Rows);

        await using var session = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), session);
        session.ApplyOnly(applied);

        foldOf(theRows.Rows).ShouldBe(before);
    }

    [Fact]
    public async Task compacting_again_finishes_what_a_partly_applied_batch_left()
    {
        aStream();
        var before = foldOf(theRows.Rows);

        await using (var session = theRows.Session())
        {
            await theCompacting.ExecuteAsync(aRequest(), session);
            session.ApplyOnly(1);
        }

        theRows.Rows.Count.ShouldBe(4);

        await using var second = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), second);
        await second.CommitAsync();

        theRows.Rows.ShouldHaveSingleItem().Body.ShouldBeOfType<Compacted<Meter>>().Snapshot.ShouldBe(before);
    }

    // ---- the archiver ----

    [Fact]
    public async Task the_archiver_sees_the_events_before_anything_is_queued()
    {
        aStream();

        var archiver = new RecordingArchiver<FakeSession>();

        await using var session = theRows.Session();
        var request = aRequest(x =>
        {
            x.Version = 3;
            x.Archiver = archiver;
        });

        await theCompacting.ExecuteAsync(request, session);

        archiver.Calls.ShouldBe(1);
        archiver.Events.Select(x => x.Version).ShouldBe([1, 2, 3]);
        archiver.QueuedWhenCalled.ShouldBeEmpty();

        // Set before the archiver ran, which reads them as the high-water mark of what it was given
        archiver.Sequence.ShouldBe(3);
        archiver.Version.ShouldBe(3);
        archiver.Operations.ShouldBeSameAs(session);
    }

    [Fact]
    public async Task every_closure_the_store_accepts_is_run()
    {
        // #423. IEventsArchiver<T> is invariant, so these are two different interfaces
        aStream();

        var archiver = new RecordingArchiver<IFakeOperations>();

        await using var session = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(x => x.Archiver = archiver), session);

        archiver.Calls.ShouldBe(1);
        session.Queued.ShouldBe(["replace", "delete", "watermark"]);
    }

    [Fact]
    public async Task an_archiver_the_store_cannot_run_refuses_the_compaction()
    {
        // Each store matched the closures it knew, skipped the rest and deleted the events anyway. Marten only
        // knows IDocumentOperations and Fisher only IDocumentSession, so each still does this to the other's.
        aStream();

        await using var session = theRows.Session();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            theCompacting.ExecuteAsync(aRequest(x => x.Archiver = new RecordingArchiver<string>()), session));

        ex.Message.ShouldContain(nameof(RecordingArchiver<string>));
        ex.Message.ShouldContain("Nothing was compacted");

        session.Queued.ShouldBeEmpty();

        await session.CommitAsync();
        theRows.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task an_archiver_that_fails_stops_the_compaction()
    {
        aStream();

        var archiver = new RecordingArchiver<FakeSession> { Failure = new TimeoutException() };

        await using var session = theRows.Session();

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            theCompacting.ExecuteAsync(aRequest(x => x.Archiver = archiver), session));

        ex.ShouldBeSameAs(archiver.Failure);
        session.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task with_no_archiver_the_compaction_goes_ahead()
    {
        aStream();

        await using var session = theRows.Session();

        (await theCompacting.ExecuteAsync(aRequest(), session)).ShouldBeTrue();

        theCompacting.TriedToArchive.ShouldBeFalse();
    }

    // ---- which stream ----

    [Fact]
    public async Task an_empty_guid_is_refused_rather_than_read()
    {
        // Marten's read once took this for "no stream predicate", so compaction folded and deleted the tenant
        await using var session = theRows.Session();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            theCompacting.ExecuteAsync(new StreamCompactingRequest<Meter>(Guid.Empty), session));

        session.Queued.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task an_empty_stream_key_is_refused_rather_than_read(string? streamKey)
    {
        await using var session = theRows.Session();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            theCompacting.ExecuteAsync(new StreamCompactingRequest<Meter>(streamKey), session));

        session.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_read_follows_the_request_rather_than_the_store_configuration()
    {
        // The Guid overload on a string store used to take the store's branch, read a null key, match nothing
        // and report success. Here the store's own Guid read is reached, and is what refuses the mismatch.
        await using var session = theRows.Session();

        await theCompacting.ExecuteAsync(aRequest(), session);

        await session.Events.Received(1).FetchStreamAsync(theStream, 0, null, 0, Arg.Any<CancellationToken>());
    }

    // ---- which tenant ----

    [Fact]
    public async Task compacting_one_tenant_leaves_the_same_numbered_events_in_another_alone()
    {
        // Per-tenant sequences: the same stream at the same sequences in both tenants, the shape that bit #5234
        aStream("alpha");
        aStream("beta");

        await using var session = theRows.Session("alpha");

        await theCompacting.ExecuteAsync(aRequest(), session);
        await session.CommitAsync();

        theRows.RowsFor("alpha").ShouldHaveSingleItem().Body.ShouldBeOfType<Compacted<Meter>>();

        var beta = theRows.RowsFor("beta").ToList();
        beta.Select(x => x.Sequence).ShouldBe([1, 2, 3, 4]);
        beta.ShouldAllBe(x => x.Body.GetType() != typeof(Compacted<Meter>));
    }

    [Fact]
    public async Task a_stream_in_another_tenant_is_not_compacted_from_this_one()
    {
        aStream("beta");

        await using var session = theRows.Session("alpha");

        (await theCompacting.ExecuteAsync(aRequest(), session)).ShouldBeFalse();

        session.Queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task every_seam_is_handed_the_one_session()
    {
        // The session is the only thing carrying the tenant, so a seam on any other would write across it
        aStream("alpha");

        await using var session = theRows.Session("alpha");

        await theCompacting.ExecuteAsync(aRequest(x => x.Archiver = new RecordingArchiver<FakeSession>()), session);

        theCompacting.Sessions.Distinct().ShouldBe([session]);
        theCompacting.Sessions.Count.ShouldBe(6);
    }

    public record MeterInstalled(string Location);

    public record MeterRead(int Reading);

    public record MeterRemoved;

    public record Meter(string Location, int Total)
    {
        public static Meter? Fold(IReadOnlyList<IEvent> events)
        {
            var (meter, remaining) = Compacted<Meter>.MaybeFastForward(null, events);

            foreach (var @event in remaining)
            {
                meter = @event.Data switch
                {
                    MeterInstalled installed => new Meter(installed.Location, 0),
                    MeterRead read => meter! with { Total = meter!.Total + read.Reading },
                    MeterRemoved => null,
                    _ => meter
                };
            }

            return meter;
        }
    }

    private class RecordingArchiver<TOperations> : IEventsArchiver<TOperations>
    {
        public int Calls { get; private set; }
        public IReadOnlyList<IEvent> Events { get; private set; } = [];
        public object? Operations { get; private set; }
        public IReadOnlyList<string> QueuedWhenCalled { get; private set; } = [];
        public long Sequence { get; private set; }
        public long Version { get; private set; }
        public Exception? Failure { get; init; }

        public Task MaybeArchiveAsync<T>(TOperations operations, StreamCompactingRequest<T> request,
            IReadOnlyList<IEvent> events, CancellationToken cancellation) where T : class
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Calls++;
            Events = events;
            Operations = operations;
            QueuedWhenCalled = (operations as FakeSession)?.Queued ?? [];
            Sequence = request.Sequence;
            Version = request.Version;

            return Task.CompletedTask;
        }
    }

    private class TestCompacting : StreamCompactingExecution<FakeSession>
    {
        public List<FakeSession> Sessions { get; } = [];
        public List<(FakeSession Session, Guid? StreamId, string? StreamKey, long Version)> Watermarks { get; } = [];
        public List<long> AppliedWatermarks { get; } = [];
        public bool TriedToArchive { get; private set; }

        protected override IEventStoreOperations EventsFor(FakeSession session)
        {
            Sessions.Add(session);
            return session.Events;
        }

        protected override ValueTask<T?> BuildAggregateAsync<T>(FakeSession session, IReadOnlyList<IEvent> events,
            CancellationToken token) where T : class
        {
            Sessions.Add(session);
            return ValueTask.FromResult((T?)(object?)Meter.Fold(events));
        }

        protected override async Task<bool> TryArchiveAsync<T>(FakeSession session, StreamCompactingRequest<T> request,
            IReadOnlyList<IEvent> events) where T : class
        {
            TriedToArchive = true;
            Sessions.Add(session);

            return await TryArchiveThroughAsync(session, request, events)
                   || await TryArchiveThroughAsync<IFakeOperations, T>(session, request, events);
        }

        protected override void DeleteEvents(FakeSession session, long[] sequences)
        {
            Sessions.Add(session);

            session.Queue("delete", rows =>
                rows.RemoveAll(x => sequences.Contains(x.Sequence) && x.Tenant == session.TenantId));
        }

        protected override void RecordCompactionWatermark(FakeSession session, Guid? streamId, string? streamKey,
            long version)
        {
            Sessions.Add(session);
            Watermarks.Add((session, streamId, streamKey, version));

            session.Queue("watermark", _ => AppliedWatermarks.Add(version));
        }
    }
}
