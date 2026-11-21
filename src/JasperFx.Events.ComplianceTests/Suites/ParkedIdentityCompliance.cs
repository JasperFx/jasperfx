using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Parked identity fixtures

public record ParkStarted;

public record ParkTicked;

public record ParkPoisoned;

/// <summary>Raised by <see cref="ParkingWatchtowerProjection" />'s side effects, one per event it saw.</summary>
public record ParkSideEffectRecorded(Guid Stream, long Sequence);

public record DepartmentPoisoned(string Department);

/// <summary>
/// The poison switch for <see cref="ParkedIdentityCompliance{TFixture,TOperations,TQuerySession}" />: a
/// <see cref="ParkPoisoned" /> event throws while its stream is in here. Static and keyed by stream, because the
/// projections are built by the suite's configuration and every store runs suites in parallel.
/// </summary>
public static class ParkingPoison
{
    private static readonly ConcurrentDictionary<string, bool> _poisoned = new();

    public static void On(object stream) => _poisoned[keyOf(stream)] = true;

    public static void Off(object stream) => _poisoned.TryRemove(keyOf(stream), out _);

    public static void ThrowIfPoisoned(IEvent @event)
    {
        var key = @event.StreamId != Guid.Empty ? @event.StreamId.ToString("D") : @event.StreamKey ?? "";
        if (_poisoned.ContainsKey(key))
        {
            throw new InvalidOperationException($"Stream {key} is poisoned");
        }
    }

    private static string keyOf(object stream) => stream is Guid id ? id.ToString("D") : stream.ToString()!;
}

/// <summary>Counts every event it folds, so a lost event and a doubled one both show.</summary>
public interface IParkedCounter
{
    int Ticks { get; }
    List<long> Sequences { get; }
}

public readonly record struct ParkedCounterId(Guid Value);

public readonly record struct ParkedCounterKey(string Value);

public partial class ParkedGuidCounter : IParkedCounter
{
    public Guid Id { get; set; }
    public int Ticks { get; set; }
    public List<long> Sequences { get; set; } = new();

    public static ParkedGuidCounter Create(IEvent<ParkStarted> e) => new() { Id = e.StreamId };

    public void Apply(IEvent<ParkTicked> e)
    {
        Ticks++;
        Sequences.Add(e.Sequence);
    }

    public void Apply(IEvent<ParkPoisoned> e)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        Ticks++;
        Sequences.Add(e.Sequence);
    }
}

public partial class ParkedStringCounter : IParkedCounter
{
    public string Id { get; set; } = string.Empty;
    public int Ticks { get; set; }
    public List<long> Sequences { get; set; } = new();

    public static ParkedStringCounter Create(IEvent<ParkStarted> e) => new() { Id = e.StreamKey! };

    public void Apply(IEvent<ParkTicked> e)
    {
        Ticks++;
        Sequences.Add(e.Sequence);
    }

    public void Apply(IEvent<ParkPoisoned> e)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        Ticks++;
        Sequences.Add(e.Sequence);
    }
}

public partial class ParkedTypedGuidCounter : IParkedCounter
{
    public ParkedCounterId Id { get; set; }
    public int Ticks { get; set; }
    public List<long> Sequences { get; set; } = new();

    public static ParkedTypedGuidCounter Create(IEvent<ParkStarted> e) => new() { Id = new ParkedCounterId(e.StreamId) };

    public void Apply(IEvent<ParkTicked> e)
    {
        Ticks++;
        Sequences.Add(e.Sequence);
    }

    public void Apply(IEvent<ParkPoisoned> e)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        Ticks++;
        Sequences.Add(e.Sequence);
    }
}

public partial class ParkedTypedStringCounter : IParkedCounter
{
    public ParkedCounterKey Id { get; set; }
    public int Ticks { get; set; }
    public List<long> Sequences { get; set; } = new();

    public static ParkedTypedStringCounter Create(IEvent<ParkStarted> e) =>
        new() { Id = new ParkedCounterKey(e.StreamKey!) };

    public void Apply(IEvent<ParkTicked> e)
    {
        Ticks++;
        Sequences.Add(e.Sequence);
    }

    public void Apply(IEvent<ParkPoisoned> e)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        Ticks++;
        Sequences.Add(e.Sequence);
    }
}

/// <summary>
/// A Guid single-stream projection with side effects: every slice raises one <see cref="ParkSideEffectRecorded" />
/// per event onto its own audit stream, so "side effects fired exactly once" is a count of committed events.
/// </summary>
public partial class ParkingWatchtowerProjection : ComplianceWatchtowerProjectionBase
{
    public const string ProjectionName = "ParkingWatchtower";

    public ParkingWatchtowerProjection()
    {
        Name = ProjectionName;
    }

    /// <summary>The audit stream the side effects of <paramref name="stream" /> are raised on.</summary>
    public static Guid AuditStreamFor(Guid stream)
    {
        var bytes = stream.ToByteArray();
        bytes[0] ^= 0x5a;
        return new Guid(bytes);
    }

    public ComplianceWatchtower Create(IEvent<ParkStarted> e) => new() { Id = e.StreamId, Name = "parking" };

    public void Apply(IEvent<ParkTicked> e, ComplianceWatchtower tower)
    {
        tower.Ticks++;
        tower.TickSequences.Add(e.Sequence);
    }

    public void Apply(IEvent<ParkPoisoned> e, ComplianceWatchtower tower)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        tower.Ticks++;
        tower.TickSequences.Add(e.Sequence);
    }

    public override ValueTask RaiseSideEffects(ComplianceOperations operations,
        IEventSlice<ComplianceWatchtower> slice)
    {
        foreach (var @event in slice.Events())
        {
            if (@event.Data is ParkTicked or ParkPoisoned)
            {
                slice.AppendEvent(AuditStreamFor(@event.StreamId),
                    new ParkSideEffectRecorded(@event.StreamId, @event.Sequence));
            }
        }

        return new ValueTask();
    }
}

/// <summary>
/// A multi-stream projection grouped by a plain delegate: <see cref="IdentityAccessClass.ScanOnly" />, so it can't
/// park and falls back to pausing its shard.
/// </summary>
public partial class ParkingDepartmentProjection : ComplianceMultiStreamProjectionBase
{
    public const string ProjectionName = "ParkingDepartment";

    public ParkingDepartmentProjection()
    {
        Name = ProjectionName;
        Identity<EmployeeHired>(x => x.Department);
        Identity<DepartmentPoisoned>(x => x.Department);
    }

    public void Apply(EmployeeHired e, ComplianceDepartment doc)
    {
        doc.HeadCount++;
        doc.Names.Add(e.Name);
    }

    public void Apply(IEvent<DepartmentPoisoned> e, ComplianceDepartment doc)
    {
        ParkingPoison.ThrowIfPoisoned(e);
        doc.AuditCount++;
    }
}

#endregion

/// <summary>
/// Parking the failing slice (marten#6003): an async single-stream projection whose fold fails for one identity parks
/// that identity in the batch that moves the shard past the failing event, holds its later events, and carries on with
/// every other identity. A replay re-folds and unparks it, a discard drops and dead-letters what still fails, and a
/// per-identity rebuild of a parked identity unparks it.
/// </summary>
/// <remarks>
/// <para>
/// The counting aggregates are the point, as in <see cref="IdentityRebuildCompliance{TFixture,TOperations,TQuerySession}" />:
/// a document whose sequence list equals its stream's sequences proves nothing was lost and nothing applied twice.
/// </para>
/// <para>
/// The matrix: the replay outcomes (replay, replay that fails again, discard) over every identity kind the store
/// supports (Guid, string, strong-typed Guid, strong-typed string); then, on the Guid projection with side effects,
/// held events, side effects fired exactly once, the durable request on the heartbeat and across a restart, a batch
/// failure rolling the unpark back, the cap and the systemic trip, unparking through a per-identity rebuild, the full
/// rebuild clearing parked rows and never parking, FetchForWriting / FetchLatest / FetchManyForWriting before, during and
/// after a park, unreadable events, the inline lifecycle, a multi-stream projection's fallback to pause, and conjoined
/// tenancy where the store has it.
/// </para>
/// </remarks>
[RequiresCapability("SupportsParkedIdentities")]
public abstract class ParkedIdentityCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static void addEvents(ComplianceStoreConfig config)
    {
        config.AddEventType<ParkStarted>();
        config.AddEventType<ParkTicked>();
        config.AddEventType<ParkPoisoned>();
        config.AddEventType<ParkSideEffectRecorded>();
    }

    private static readonly Action<ComplianceStoreConfig> _watchtowerConfiguration = config =>
    {
        config.SchemaName = "compliance_parking";
        addEvents(config);
        config.AddProjection(new ParkingWatchtowerProjection(), ProjectionLifecycle.Async);
    };

    private static readonly Action<ComplianceStoreConfig> _inlineConfiguration = config =>
    {
        config.SchemaName = "compliance_parking_inline";
        addEvents(config);
        config.AddProjection(new ParkingWatchtowerProjection(), ProjectionLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_parking_conjoined";
        config.ConjoinedEventTenancy = true;
        addEvents(config);
        config.AddProjection(new ParkingWatchtowerProjection(), ProjectionLifecycle.Async);
    };

    private static readonly Action<ComplianceStoreConfig> _multiStreamConfiguration = config =>
    {
        config.SchemaName = "compliance_parking_multi";
        config.AddEventType<EmployeeHired>();
        config.AddEventType<DepartmentPoisoned>();
        config.AddProjection(new ParkingDepartmentProjection(), ProjectionLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _watchtowerConfiguration;

    private IEventStore<TOperations, TQuerySession> Store => (IEventStore<TOperations, TQuerySession>)EventStore;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsParkedIdentities,
            "This event store does not implement the parked-identity seams (marten#6003)");
    }

    private async Task reconfigureAsync(Action<ComplianceStoreConfig> configuration)
    {
        await theFixture.ConfigureAsync(configuration);
        await theFixture.CleanEventDataAsync();
    }

    private void park(Action<ErrorHandlingOptions>? alter = null)
    {
        Store.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Park;
        alter?.Invoke(Store.ContinuousErrors);
    }

    private async Task<IEventDatabase> theDatabaseAsync() => (await EventStore.AllDatabases()).First();

    private ShardName shardFor(string projectionName) => EventStore.RegisteredShardNames()
        .Single(x => x.Name == projectionName);

    private static object[] ticks(int count) => Enumerable.Range(0, count).Select(_ => (object)new ParkTicked()).ToArray();

    #region identity kinds

    /// <summary>The identity kinds of the matrix.</summary>
    public static TheoryData<string> IdentityKinds => new() { "guid", "string", "typed-guid", "typed-string" };

    private sealed class Kind
    {
        public required Action<ComplianceStoreConfig> Configuration { get; init; }
        public required Type AggregateType { get; init; }
        public required bool StringStreams { get; init; }

        // The aggregate's own identity, given the stream's id or key
        public required Func<object, object> Identity { get; init; }
    }

    private static Action<ComplianceStoreConfig> snapshotConfiguration<TDoc>(string schema, bool stringStreams,
        Type? valueType) where TDoc : notnull
        => config =>
        {
            config.SchemaName = schema;
            if (stringStreams) config.StreamIdentity = StreamIdentity.AsString;
            addEvents(config);
            if (valueType == typeof(ParkedCounterId)) config.RegisterValueType<ParkedCounterId>();
            if (valueType == typeof(ParkedCounterKey)) config.RegisterValueType<ParkedCounterKey>();
            config.Snapshot<TDoc>(SnapshotLifecycle.Async);
        };

    private static readonly Dictionary<string, Kind> _kinds = new()
    {
        ["guid"] = new Kind
        {
            Configuration = snapshotConfiguration<ParkedGuidCounter>("compliance_parked_guid", false, null),
            AggregateType = typeof(ParkedGuidCounter),
            StringStreams = false,
            Identity = x => x
        },
        ["string"] = new Kind
        {
            Configuration = snapshotConfiguration<ParkedStringCounter>("compliance_parked_string", true, null),
            AggregateType = typeof(ParkedStringCounter),
            StringStreams = true,
            Identity = x => x
        },
        ["typed-guid"] = new Kind
        {
            Configuration = snapshotConfiguration<ParkedTypedGuidCounter>("compliance_parked_typed_guid", false,
                typeof(ParkedCounterId)),
            AggregateType = typeof(ParkedTypedGuidCounter),
            StringStreams = false,
            Identity = x => new ParkedCounterId((Guid)x)
        },
        ["typed-string"] = new Kind
        {
            Configuration = snapshotConfiguration<ParkedTypedStringCounter>("compliance_parked_typed_string", true,
                typeof(ParkedCounterKey)),
            AggregateType = typeof(ParkedTypedStringCounter),
            StringStreams = true,
            Identity = x => new ParkedCounterKey((string)x)
        }
    };

    private string projectionNameFor(Kind kind) => EventStore.RegisteredShardNames().Single().Name;

    private async Task<object> aStreamAsync(Kind kind, params object[] events)
    {
        object stream = kind.StringStreams ? $"parked/{Guid.NewGuid():N}" : Guid.NewGuid();

        await using var session = OpenSession();
        var all = new object[] { new ParkStarted() }.Concat(events).ToArray();
        if (stream is Guid id)
        {
            EventsFor(session).StartStream(kind.AggregateType, id, all);
        }
        else
        {
            EventsFor(session).StartStream(kind.AggregateType, (string)stream, all);
        }

        await SaveChangesAsync(session);
        return stream;
    }

    private async Task appendAsync(object stream, params object[] events)
    {
        await using var session = OpenSession();
        if (stream is Guid id)
        {
            EventsFor(session).Append(id, events);
        }
        else
        {
            EventsFor(session).Append((string)stream, events);
        }

        await SaveChangesAsync(session);
    }

    private async Task<IReadOnlyList<IEvent>> streamAsync(object stream)
    {
        await using var session = OpenSession();
        return stream is Guid id
            ? await EventsFor(session).FetchStreamAsync(id, token: Cancellation)
            : await EventsFor(session).FetchStreamAsync((string)stream, token: Cancellation);
    }

    private async Task<IParkedCounter?> loadCounterAsync(Kind kind, object stream)
    {
        await using var session = OpenSession();
        var id = kind.Identity(stream);

        return kind.AggregateType.Name switch
        {
            nameof(ParkedGuidCounter) => await LoadDocumentAsync<ParkedGuidCounter>(session, id),
            nameof(ParkedStringCounter) => await LoadDocumentAsync<ParkedStringCounter>(session, id),
            nameof(ParkedTypedGuidCounter) => await LoadDocumentAsync<ParkedTypedGuidCounter>(session, id),
            _ => await LoadDocumentAsync<ParkedTypedStringCounter>(session, id)
        };
    }

    private async Task<List<long>> handledSequencesAsync(object stream, Func<IEvent, bool>? except = null)
        => (await streamAsync(stream))
            .Where(x => x.Data is ParkTicked or ParkPoisoned)
            .Where(x => except == null || !except(x))
            .Select(x => x.Sequence)
            .ToList();

    // A stream whose first batch commits (so the document exists), then a poisoned event and a tick after it
    private async Task<(IProjectionDaemon Daemon, object Parked, object Bystander, long ResumeAfter)>
        aParkedIdentityAsync(Kind kind)
    {
        await reconfigureAsync(kind.Configuration);
        park();

        var parked = await aStreamAsync(kind, ticks(2));
        var bystander = await aStreamAsync(kind, ticks(1));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var resumeAfter = (await streamAsync(bystander)).Last().Sequence;

        ParkingPoison.On(parked);
        await appendAsync(parked, new ParkPoisoned(), new ParkTicked());
        await appendAsync(bystander, new ParkTicked());
        await WaitForNonStaleProjectionDataAsync(_timeout);

        return (daemon, parked, bystander, resumeAfter);
    }

    private async Task<ParkedIdentity> theParkedRowAsync(string projectionName, object identityText)
    {
        var rows = await (await theDatabaseAsync()).FetchParkedIdentitiesAsync(
            new ParkedIdentityQuery { ProjectionName = projectionName }, Cancellation);

        return rows.ShouldHaveSingleItem($"one parked row for {identityText}");
    }

    private async Task<IReadOnlyList<ParkedIdentity>> parkedRowsAsync(string? projectionName = null)
        => await (await theDatabaseAsync()).FetchParkedIdentitiesAsync(
            new ParkedIdentityQuery { ProjectionName = projectionName }, Cancellation);

    #endregion

    [Theory]
    [MemberData(nameof(IdentityKinds))]
    public async Task a_failing_identity_parks_and_a_replay_unparks_it(string kindName)
    {
        SkipUnlessSupported();
        var kind = _kinds[kindName];

        var (daemon, parked, bystander, _) = await aParkedIdentityAsync(kind);
        var name = projectionNameFor(kind);

        try
        {
            var row = await theParkedRowAsync(name, parked);
            row.IdentityText.ShouldBe(ParkedIdentityText.Format(parked));
            row.Category.ShouldBe(ShardFailureCategory.ApplyEvent);
            row.EventTypeName.ShouldBe(EventTypeNameFor<ParkPoisoned>());
            daemon.IsIdentityParked(name, null, kind.Identity(parked)).ShouldBeTrue();

            // Frozen at the last good state; everyone else carried on
            (await loadCounterAsync(kind, parked)).ShouldNotBeNull().Ticks.ShouldBe(2);
            (await loadCounterAsync(kind, bystander)).ShouldNotBeNull().Sequences
                .ShouldBe(await handledSequencesAsync(bystander));

            // Held while parked
            await appendAsync(parked, new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);
            (await loadCounterAsync(kind, parked))!.Ticks.ShouldBe(2);

            ParkingPoison.Off(parked);
            var result = await daemon.ReplayParkedIdentityAsync(name, null, kind.Identity(parked), Cancellation);

            result.Outcome.ShouldBe(ParkedReplayOutcome.Replayed);
            (await loadCounterAsync(kind, parked))!.Sequences.ShouldBe(await handledSequencesAsync(parked));
            (await parkedRowsAsync(name)).ShouldBeEmpty();
            daemon.IsIdentityParked(name, null, kind.Identity(parked)).ShouldBeFalse();

            // And it is live again
            await appendAsync(parked, new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);
            (await loadCounterAsync(kind, parked))!.Sequences.ShouldBe(await handledSequencesAsync(parked));
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Theory]
    [MemberData(nameof(IdentityKinds))]
    public async Task a_replay_that_fails_again_leaves_the_identity_parked_with_the_new_error(string kindName)
    {
        SkipUnlessSupported();
        var kind = _kinds[kindName];

        var (daemon, parked, _, _) = await aParkedIdentityAsync(kind);
        var name = projectionNameFor(kind);

        try
        {
            var result = await daemon.ReplayParkedIdentityAsync(name, null, kind.Identity(parked), Cancellation);

            result.Outcome.ShouldBe(ParkedReplayOutcome.FailedAgain);
            result.Exception.ShouldNotBeNull();

            daemon.IsIdentityParked(name, null, kind.Identity(parked)).ShouldBeTrue();
            (await loadCounterAsync(kind, parked))!.Ticks.ShouldBe(2);

            // Written out of band, so give it a moment
            var row = await eventuallyAsync(async () =>
            {
                var current = await theParkedRowAsync(name, parked);
                return current.ReplayAttempts > 0 ? current : null;
            });

            row.ReplayAttempts.ShouldBe(1);
            row.LastAttemptError.ShouldNotBeNullOrEmpty();
            row.LastAttemptAt.ShouldNotBeNull();

            // The shard keeps running for everyone else
            daemon.StatusFor(shardFor(name).Identity).ShouldBe(AgentStatus.Running);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Theory]
    [MemberData(nameof(IdentityKinds))]
    public async Task a_discard_dead_letters_what_still_fails_applies_the_rest_and_unparks(string kindName)
    {
        SkipUnlessSupported();
        var kind = _kinds[kindName];

        var (daemon, parked, _, _) = await aParkedIdentityAsync(kind);
        var name = projectionNameFor(kind);

        try
        {
            await appendAsync(parked, new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);

            var result = await daemon.DiscardParkedIdentityAsync(name, null, kind.Identity(parked), Cancellation);
            result.Outcome.ShouldBe(ParkedReplayOutcome.Discarded);

            var poison = (await streamAsync(parked)).Single(x => x.Data is ParkPoisoned);

            (await loadCounterAsync(kind, parked))!.Sequences
                .ShouldBe(await handledSequencesAsync(parked, x => x.Sequence == poison.Sequence));
            (await parkedRowsAsync(name)).ShouldBeEmpty();

            var deadLetters = await eventuallyAsync(async () =>
            {
                var letters = await (await theDatabaseAsync()).QueryDeadLetterEventsAsync(shardFor(name), null, 0,
                    100, Cancellation);
                return letters.Count > 0 ? letters : null;
            });

            deadLetters.ShouldHaveSingleItem().EventSequence.ShouldBe(poison.Sequence);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task nothing_is_lost_or_applied_twice_across_park_and_replay_during_appends()
    {
        SkipUnlessSupported();
        var kind = _kinds["guid"];

        var (daemon, parked, bystander, _) = await aParkedIdentityAsync(kind);
        var name = projectionNameFor(kind);

        try
        {
            using var appending = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            var appender = Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++)
                {
                    await appendAsync(parked, new ParkTicked());
                    await appendAsync(bystander, new ParkTicked(), new ParkTicked());
                }
            }, appending.Token);

            await Task.Delay(50, Cancellation);
            ParkingPoison.Off(parked);
            (await daemon.ReplayParkedIdentityAsync(name, null, parked, Cancellation)).Outcome
                .ShouldBe(ParkedReplayOutcome.Replayed);

            await appender;
            await WaitForNonStaleProjectionDataAsync(_timeout);

            (await loadCounterAsync(kind, parked))!.Sequences.ShouldBe(await handledSequencesAsync(parked));
            (await loadCounterAsync(kind, bystander))!.Sequences.ShouldBe(await handledSequencesAsync(bystander));
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task replaying_all_and_the_oldest_unparks_in_parked_order()
    {
        SkipUnlessSupported();
        var kind = _kinds["guid"];
        await reconfigureAsync(kind.Configuration);
        park();

        var streams = new List<object>();
        for (var i = 0; i < 3; i++) streams.Add(await aStreamAsync(kind, ticks(1)));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        var name = projectionNameFor(kind);

        try
        {
            // One at a time, so they park in order
            foreach (var stream in streams)
            {
                ParkingPoison.On(stream);
                await appendAsync(stream, new ParkPoisoned());
                await WaitForNonStaleProjectionDataAsync(_timeout);
            }

            (await parkedRowsAsync(name)).Count.ShouldBe(3);
            foreach (var stream in streams) ParkingPoison.Off(stream);

            var oldest = await daemon.ReplayParkedIdentitiesAsync(name, null, ParkedSelection.OldestFirst(1),
                Cancellation);
            oldest.ShouldHaveSingleItem().Identity.ShouldBe(streams[0]);

            var rest = await daemon.ReplayParkedIdentitiesAsync(name, null, ParkedSelection.All, Cancellation);
            rest.Select(x => x.Identity).ShouldBe(streams.Skip(1));
            rest.ShouldAllBe(x => x.Outcome == ParkedReplayOutcome.Replayed);

            (await parkedRowsAsync(name)).ShouldBeEmpty();
            foreach (var stream in streams)
            {
                (await loadCounterAsync(kind, stream))!.Sequences.ShouldBe(await handledSequencesAsync(stream));
            }
        }
        finally
        {
            foreach (var stream in streams) ParkingPoison.Off(stream);
        }
    }

    #region the Guid projection with side effects

    private async Task<Guid> aTowerAsync(params object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceWatchtower>(id, new object[] { new ParkStarted() }.Concat(events).ToArray());
        await SaveChangesAsync(session);
        return id;
    }

    private async Task<ComplianceWatchtower?> loadTowerAsync(Guid id)
    {
        await using var session = OpenSession();
        return await LoadDocumentAsync<ComplianceWatchtower>(session, id);
    }

    private async Task theTowerMatchesItsStreamAsync(Guid id, Func<IEvent, bool>? except = null)
    {
        var tower = (await loadTowerAsync(id)).ShouldNotBeNull($"tower {id} has no document");
        tower.TickSequences.ShouldBe(await handledSequencesAsync(id, except), $"tower {id}: lost or doubled events");
    }

    // The side effects committed for the stream's events: one per event, exactly once
    private async Task<List<long>> sideEffectsForAsync(Guid id)
    {
        await using var session = OpenSession();
        var audit = await EventsFor(session).FetchStreamAsync(ParkingWatchtowerProjection.AuditStreamFor(id),
            token: Cancellation);
        return audit.Select(x => x.Data).OfType<ParkSideEffectRecorded>().Select(x => x.Sequence).OrderBy(x => x)
            .ToList();
    }

    private async Task<(IProjectionDaemon Daemon, Guid Parked, Guid Bystander)> aParkedTowerAsync(
        Action<ErrorHandlingOptions>? alter = null)
    {
        park(alter);

        var parked = await aTowerAsync(ticks(2));
        var bystander = await aTowerAsync(ticks(1));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        ParkingPoison.On(parked);
        await appendAsync(parked, new ParkPoisoned(), new ParkTicked());
        await appendAsync(bystander, new ParkTicked());
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem();
        return (daemon, parked, bystander);
    }

    [Fact]
    public async Task side_effects_of_the_held_events_fire_exactly_once_on_replay()
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTowerAsync();

        try
        {
            await appendAsync(parked, new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);

            // Nothing for the events of the failed batch or the held ones yet
            var handled = await handledSequencesAsync(parked);
            (await sideEffectsForAsync(parked)).ShouldBe(handled.Take(2));

            ParkingPoison.Off(parked);
            (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

            await WaitForNonStaleProjectionDataAsync(_timeout);
            (await sideEffectsForAsync(parked)).ShouldBe(handled);
            (await sideEffectsForAsync(bystander)).ShouldBe(await handledSequencesAsync(bystander));
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task side_effects_fire_once_after_a_replay_that_failed_first()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.FailedAgain);

            ParkingPoison.Off(parked);
            (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

            await WaitForNonStaleProjectionDataAsync(_timeout);
            (await sideEffectsForAsync(parked)).ShouldBe(await handledSequencesAsync(parked));
            await theTowerMatchesItsStreamAsync(parked);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task side_effects_fire_once_for_what_a_discard_applies()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            (await daemon.DiscardParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Discarded);

            await WaitForNonStaleProjectionDataAsync(_timeout);

            var poison = (await streamAsync(parked)).Single(x => x.Data is ParkPoisoned).Sequence;
            (await sideEffectsForAsync(parked)).ShouldBe(await handledSequencesAsync(parked, x => x.Sequence == poison));
            await theTowerMatchesItsStreamAsync(parked, x => x.Sequence == poison);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task the_shard_reports_how_many_identities_it_holds_parked()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            var agent = daemon.CurrentAgents()
                .Single(x => x.Name.Name == ParkingWatchtowerProjection.ProjectionName);
            agent.ParkedCount.ShouldBe(1);

            var counts = await (await theDatabaseAsync()).FetchParkedCountsAsync(null, Cancellation);
            counts.ShouldHaveSingleItem().Count.ShouldBe(1);

            ParkingPoison.Off(parked);
            await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation);

            agent.ParkedCount.ShouldBe(0);
            (await (await theDatabaseAsync()).FetchParkedCountsAsync(null, Cancellation)).ShouldBeEmpty();
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task a_durable_request_is_run_by_the_shard_on_its_heartbeat()
    {
        SkipUnlessSupported();
        var (_, parked, _) = await aParkedTowerAsync();

        try
        {
            ParkingPoison.Off(parked);

            var marked = await (await theDatabaseAsync()).RequestParkedActionAsync(new ParkedActionRequest
            {
                ProjectionName = ParkingWatchtowerProjection.ProjectionName,
                Action = ParkedAction.Replay,
                IdentityText = ParkedIdentityText.Format(parked)
            }, Cancellation);

            marked.ShouldBe(1);

            // The heartbeat runs every ten seconds
            await eventuallyAsync(async () =>
                (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).Count == 0 ? "gone" : null,
                TimeSpan.FromSeconds(45));

            await theTowerMatchesItsStreamAsync(parked);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task parked_identities_and_a_pending_request_survive_a_restart()
    {
        SkipUnlessSupported();
        var (_, parked, _) = await aParkedTowerAsync();

        try
        {
            await (await theDatabaseAsync()).RequestParkedActionAsync(new ParkedActionRequest
            {
                ProjectionName = ParkingWatchtowerProjection.ProjectionName,
                Action = ParkedAction.Replay
            }, Cancellation);

            // Gone before the heartbeat could run it: nothing was committed, the row and its request stay
            await StopDaemonAsync();
            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem()
                .RequestedAction.ShouldBe(ParkedAction.Replay);

            ParkingPoison.Off(parked);
            await appendAsync(parked, new ParkTicked());

            var daemon = await StartDaemonAsync();
            await WaitForNonStaleProjectionDataAsync(_timeout);

            // jasperfx#1263: the shard loads its parked set as it starts and runs the pending request then, so the
            // row goes without waiting for a heartbeat
            await eventuallyAsync(async () =>
                (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).Count == 0 ? "gone" : null,
                TimeSpan.FromSeconds(45));
            daemon.IsIdentityParked(ParkingWatchtowerProjection.ProjectionName, null, parked).ShouldBeFalse();

            await theTowerMatchesItsStreamAsync(parked);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task a_batch_that_fails_rolls_back_the_unpark_with_the_document()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            ParkingPoison.Off(parked);
            var database = await theDatabaseAsync();
            var shard = shardFor(ParkingWatchtowerProjection.ProjectionName);

            var request = new IdentityRebuildRequest(StorageConstants.DefaultTenantId, parked)
            {
                // Move the progression out from under the batch, so its floor-checked update fails the commit
                InSameBatch = async _ =>
                    await Store.RewindAgentProgressAsync(database, shard.Identity, Cancellation, 1_000_000)
            };

            await Should.ThrowAsync<Exception>(() =>
                daemon.RebuildIdentityAsync(ParkingWatchtowerProjection.ProjectionName, request, Cancellation));

            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem();
            (await loadTowerAsync(parked))!.Ticks.ShouldBe(2);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task a_per_identity_rebuild_of_a_parked_identity_unparks_it()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            ParkingPoison.Off(parked);

            var result = await daemon.RebuildIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation);

            result.Unparked.ShouldBeTrue();
            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldBeEmpty();
            await theTowerMatchesItsStreamAsync(parked);

            await appendAsync(parked, new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);
            await theTowerMatchesItsStreamAsync(parked);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task passing_the_cap_pauses_the_shard_and_keeps_the_parks()
    {
        SkipUnlessSupported();
        park(x => x.MaxParkedIdentitiesPerShard = 1);

        var first = await aTowerAsync(ticks(1));
        var second = await aTowerAsync(ticks(1));

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        var shard = shardFor(ParkingWatchtowerProjection.ProjectionName);

        try
        {
            ParkingPoison.On(first);
            await appendAsync(first, new ParkPoisoned());
            await WaitForNonStaleProjectionDataAsync(_timeout);

            ParkingPoison.On(second);
            await appendAsync(second, new ParkPoisoned());

            await eventuallyAsync(() =>
                Task.FromResult(daemon.StatusFor(shard.Identity) == AgentStatus.Paused ? "paused" : null));

            daemon.CurrentAgents().Single(x => x.Name.Identity == shard.Identity).Failure.ShouldNotBeNull()
                .Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);

            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem()
                .IdentityText.ShouldBe(ParkedIdentityText.Format(first));
        }
        finally
        {
            ParkingPoison.Off(first);
            ParkingPoison.Off(second);
        }
    }

    [Fact]
    public async Task a_batch_that_would_park_most_of_its_identities_pauses_instead()
    {
        SkipUnlessSupported();
        park(x => x.ParkedTripMinimum = 3);

        var daemon = await StartDaemonAsync();
        var shard = shardFor(ParkingWatchtowerProjection.ProjectionName);

        var poisoned = new List<Guid>();
        try
        {
            // Four of five in one batch
            await using (var session = OpenSession())
            {
                for (var i = 0; i < 5; i++)
                {
                    var id = Guid.NewGuid();
                    if (i < 4)
                    {
                        poisoned.Add(id);
                        ParkingPoison.On(id);
                    }

                    EventsFor(session).StartStream<ComplianceWatchtower>(id, new ParkStarted(), new ParkPoisoned());
                }

                await SaveChangesAsync(session);
            }

            await eventuallyAsync(() =>
                Task.FromResult(daemon.StatusFor(shard.Identity) == AgentStatus.Paused ? "paused" : null));

            daemon.CurrentAgents().Single(x => x.Name.Identity == shard.Identity).Failure.ShouldNotBeNull()
                .Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);

            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldBeEmpty();
        }
        finally
        {
            foreach (var id in poisoned) ParkingPoison.Off(id);
        }
    }

    [Fact]
    public async Task a_full_rebuild_clears_the_parked_rows()
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTowerAsync();

        try
        {
            ParkingPoison.Off(parked);
            await daemon.RebuildProjectionAsync(ParkingWatchtowerProjection.ProjectionName, _timeout, Cancellation);
            await WaitForNonStaleProjectionDataAsync(_timeout);

            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldBeEmpty();
            daemon.IsIdentityParked(ParkingWatchtowerProjection.ProjectionName, null, parked).ShouldBeFalse();
            await theTowerMatchesItsStreamAsync(parked);
            await theTowerMatchesItsStreamAsync(bystander);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task a_full_rebuild_never_parks()
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTowerAsync();

        try
        {
            // Still poisoned: the rebuild runs under the rebuild error options, and does not park
            await Should.ThrowAsync<Exception>(() =>
                daemon.RebuildProjectionAsync(ParkingWatchtowerProjection.ProjectionName, TimeSpan.FromSeconds(20),
                    Cancellation));

            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldBeEmpty();
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task fetching_for_writing_and_fetching_latest_see_the_right_aggregate_around_a_park()
    {
        SkipUnlessSupported();
        park();

        var parked = await aTowerAsync(ticks(2));
        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        async Task<(ComplianceWatchtower? Writing, ComplianceWatchtower? Latest, ComplianceWatchtower? Many)> fetchAsync()
        {
            await using var session = OpenSession();
            var writing = await EventsFor(session).FetchForWriting<ComplianceWatchtower>(parked, Cancellation);
            var latest = await EventsFor(session).FetchLatest<ComplianceWatchtower>(parked, Cancellation);
            var many = await EventsFor(session).FetchManyForWriting<ComplianceWatchtower>([parked], Cancellation);
            return (writing.Aggregate, latest, many.Single().Aggregate);
        }

        try
        {
            // Before the park
            var before = await fetchAsync();
            before.Writing.ShouldNotBeNull().Ticks.ShouldBe(2);
            before.Latest.ShouldNotBeNull().Ticks.ShouldBe(2);
            before.Many.ShouldNotBeNull().Ticks.ShouldBe(2);

            ParkingPoison.On(parked);
            await appendAsync(parked, new ParkPoisoned(), new ParkTicked());
            await WaitForNonStaleProjectionDataAsync(_timeout);
            (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem();

            // While parked: the stored document is frozen at the last good state
            (await loadTowerAsync(parked))!.Ticks.ShouldBe(2);

            // ...and a fetch that folds the poisoned event fails the same way the projection did
            await Should.ThrowAsync<Exception>(fetchAsync);

            // Once the code is fixed, a fetch folds everything even though the stored document is still frozen
            ParkingPoison.Off(parked);
            var expected = await handledSequencesAsync(parked);
            var during = await fetchAsync();
            during.Writing.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            during.Latest.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            during.Many.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            (await loadTowerAsync(parked))!.Ticks.ShouldBe(2);

            // After the replay the stored document agrees
            (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

            var after = await fetchAsync();
            after.Writing.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            after.Latest.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            after.Many.ShouldNotBeNull().TickSequences.ShouldBe(expected);
            await theTowerMatchesItsStreamAsync(parked);
        }
        finally
        {
            ParkingPoison.Off(parked);
        }
    }

    [Fact]
    public async Task an_unreadable_event_parks_its_stream_and_a_discard_dead_letters_it()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsUnreadableEvents,
            "This event store's fixture cannot make a stored event unreadable");

        park(x => x.SerializationErrors = ProjectionErrorAction.Park);

        var parked = await aTowerAsync(ticks(2));
        var bystander = await aTowerAsync(ticks(1));
        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await appendAsync(parked, new ParkTicked(), new ParkTicked());
        var unreadable = (await streamAsync(parked)).Last().Sequence - 1;
        await theFixture.MakeEventUnreadableAsync(unreadable);
        await appendAsync(bystander, new ParkTicked());

        await WaitForNonStaleProjectionDataAsync(_timeout);

        var row = (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem();
        row.Category.ShouldBe(ShardFailureCategory.EventSerialization);
        row.FirstFailingSequence.ShouldBe(unreadable);
        row.IdentityText.ShouldBe(ParkedIdentityText.Format(parked));
        (await loadTowerAsync(parked))!.Ticks.ShouldBe(2);
        await theTowerMatchesItsStreamAsync(bystander);

        // A replay reads it with its own settings, never the store's: it fails again
        (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
            Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.FailedAgain);

        (await daemon.DiscardParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, null, parked,
            Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Discarded);

        (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldBeEmpty();
        (await loadTowerAsync(parked))!.Ticks.ShouldBe(3);
    }

    [Fact]
    public async Task an_inline_projection_never_parks()
    {
        SkipUnlessSupported();
        await reconfigureAsync(_inlineConfiguration);
        park();

        var id = Guid.NewGuid();
        ParkingPoison.On(id);
        try
        {
            await Should.ThrowAsync<Exception>(async () =>
            {
                await using var session = OpenSession();
                EventsFor(session).StartStream<ComplianceWatchtower>(id, new ParkStarted(), new ParkPoisoned());
                await SaveChangesAsync(session);
            });

            (await parkedRowsAsync()).ShouldBeEmpty();
        }
        finally
        {
            ParkingPoison.Off(id);
        }
    }

    [Fact]
    public async Task a_multi_stream_projection_that_cannot_find_one_identity_pauses_instead()
    {
        SkipUnlessSupported();
        await reconfigureAsync(_multiStreamConfiguration);
        park();

        var daemon = await StartDaemonAsync();
        var shard = shardFor(ParkingDepartmentProjection.ProjectionName);

        var stream = Guid.NewGuid();
        ParkingPoison.On(stream);
        try
        {
            await using (var session = OpenSession())
            {
                EventsFor(session).StartStream(stream, new EmployeeHired("Kim", "parking"),
                    new DepartmentPoisoned("parking"));
                await SaveChangesAsync(session);
            }

            await eventuallyAsync(() =>
                Task.FromResult(daemon.StatusFor(shard.Identity) == AgentStatus.Paused ? "paused" : null));

            (await parkedRowsAsync()).ShouldBeEmpty();

            var usage = await EventStore.TryCreateUsage(Cancellation);
            var descriptor = usage.ShouldNotBeNull().Subscriptions
                .Single(x => x.Name == ParkingDepartmentProjection.ProjectionName);
            descriptor.IdentityAccess.ShouldBe(IdentityAccessClass.ScanOnly);
            descriptor.CanPark.ShouldBeFalse();
        }
        finally
        {
            ParkingPoison.Off(stream);
        }
    }

    [Fact]
    public async Task parking_one_tenants_identity_leaves_another_tenants_alone()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy");
        await reconfigureAsync(_conjoinedConfiguration);
        park();

        var id = Guid.NewGuid();
        var database = await theDatabaseAsync();
        foreach (var tenant in new[] { "red", "blue" })
        {
            await using var session = Store.OpenSession(database, tenant);
            EventsFor(session).StartStream<ComplianceWatchtower>(id, new ParkStarted(), new ParkTicked());
            await SaveChangesAsync(session);
        }

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        async Task<ComplianceWatchtower?> loadForAsync(string tenant)
        {
            await using var session = Store.OpenSession(database, tenant);
            return await LoadDocumentAsync<ComplianceWatchtower>(session, id);
        }

        ParkingPoison.On(id);
        try
        {
            await using (var session = Store.OpenSession(database, "red"))
            {
                EventsFor(session).Append(id, new ParkPoisoned(), new ParkTicked());
                await SaveChangesAsync(session);
            }

            await WaitForNonStaleProjectionDataAsync(_timeout);
            ParkingPoison.Off(id);

            await using (var session = Store.OpenSession(database, "blue"))
            {
                EventsFor(session).Append(id, new ParkTicked());
                await SaveChangesAsync(session);
            }

            await WaitForNonStaleProjectionDataAsync(_timeout);

            var row = (await parkedRowsAsync(ParkingWatchtowerProjection.ProjectionName)).ShouldHaveSingleItem();
            row.TenantId.ShouldBe("red");
            (await loadForAsync("red"))!.Ticks.ShouldBe(1);
            (await loadForAsync("blue"))!.Ticks.ShouldBe(2);

            (await daemon.ReplayParkedIdentityAsync(ParkingWatchtowerProjection.ProjectionName, "red", id,
                Cancellation)).Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

            (await loadForAsync("red"))!.Ticks.ShouldBe(3);
            (await loadForAsync("blue"))!.Ticks.ShouldBe(2);
        }
        finally
        {
            ParkingPoison.Off(id);
        }
    }

    #endregion

    private async Task<T> eventuallyAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? _timeout);
        while (true)
        {
            var value = await probe();
            if (value != null) return value;

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time");
            }

            await Task.Delay(100, Cancellation);
        }
    }
}
