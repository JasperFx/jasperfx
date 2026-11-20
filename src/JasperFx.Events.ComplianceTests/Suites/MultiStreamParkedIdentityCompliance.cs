using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Multi-stream parked identity fixtures

/// <summary>One tick for each tally it names. Ignored names: a stream- or tag-grouped projection finds its own.</summary>
public record TallyTicked(Guid[] Tallies);

/// <summary>Like <see cref="TallyTicked" />, but throws for a tally in <see cref="TallyPoison" />.</summary>
public record TallyPoisoned(Guid[] Tallies);

/// <summary>Raised by the tally projections' side effects, one per event a tally applied.</summary>
public record TallySideEffect(Guid Tally, long Sequence);

/// <summary>The DCB tag the tag-grouped tally projection slices by.</summary>
public readonly record struct TallyTag(Guid Value);

/// <summary>Earned on a card; a lookup grouper finds the member who owns the card.</summary>
public record ParkingPointsEarned(Guid CardId, int Points);

/// <summary>The poison switch of the multi-stream parking suite, by the identity the fold runs for.</summary>
public static class TallyPoison
{
    private static readonly ConcurrentDictionary<Guid, bool> _poisoned = new();

    public static void On(Guid identity) => _poisoned[identity] = true;

    public static void Off(Guid identity) => _poisoned.TryRemove(identity, out _);

    public static void ThrowIfPoisoned(Guid identity)
    {
        if (_poisoned.ContainsKey(identity))
        {
            throw new InvalidOperationException($"Tally {identity} is poisoned");
        }
    }

    /// <summary>The stream a tally's side effects are raised on.</summary>
    public static Guid AuditStreamFor(Guid tally)
    {
        var bytes = tally.ToByteArray();
        bytes[0] ^= 0x3c;
        bytes[15] ^= 0x3c;
        return new Guid(bytes);
    }

    internal static ValueTask RaiseFor(IEventSlice<ComplianceBalance> slice)
    {
        if (slice.Snapshot is not { } tally) return new ValueTask();

        foreach (var @event in slice.Events())
        {
            if (@event.Data is TallyTicked or TallyPoisoned)
            {
                slice.AppendEvent(AuditStreamFor(tally.Id), new TallySideEffect(tally.Id, @event.Sequence));
            }
        }

        return new ValueTask();
    }
}

/// <summary>Class A by stream: <c>IdentityFromStream</c>. Each tally is its own stream.</summary>
public partial class ParkingStreamTallyProjection : ComplianceBalanceProjectionBase
{
    public const string ProjectionName = "ParkingStreamTally";

    public ParkingStreamTallyProjection()
    {
        Name = ProjectionName;
        IdentityFromStream<TallyTicked>();
        IdentityFromStream<TallyPoisoned>();
    }

    public void Apply(TallyTicked e, ComplianceBalance tally) => tally.Amount++;

    public void Apply(TallyPoisoned e, ComplianceBalance tally)
    {
        TallyPoison.ThrowIfPoisoned(tally.Id);
        tally.Amount++;
    }

    public override ValueTask RaiseSideEffects(ComplianceOperations operations, IEventSlice<ComplianceBalance> slice)
        => TallyPoison.RaiseFor(slice);
}

/// <summary>Class A by tag: <c>IdentityFromTag</c>. An event tagged with two tallies goes to both.</summary>
public partial class ParkingTagTallyProjection : ComplianceBalanceProjectionBase
{
    public const string ProjectionName = "ParkingTagTally";

    public ParkingTagTallyProjection()
    {
        Name = ProjectionName;
        IdentityFromTag<TallyTag>();
        IncludeType<TallyTicked>();
        IncludeType<TallyPoisoned>();
    }

    public void Apply(TallyTicked e, ComplianceBalance tally) => tally.Amount++;

    public void Apply(TallyPoisoned e, ComplianceBalance tally)
    {
        TallyPoison.ThrowIfPoisoned(tally.Id);
        tally.Amount++;
    }

    public override ValueTask RaiseSideEffects(ComplianceOperations operations, IEventSlice<ComplianceBalance> slice)
        => TallyPoison.RaiseFor(slice);
}

/// <summary>
/// Class B: plain <c>Identities</c> delegates, which can't be inverted, made findable by the slice index.
/// </summary>
public partial class ParkingIndexedTallyProjection : ComplianceBalanceProjectionBase
{
    public const string ProjectionName = "ParkingIndexedTally";

    public ParkingIndexedTallyProjection() : this(ProjectionName, null)
    {
    }

    public ParkingIndexedTallyProjection(string name, SliceIndexRetention? retention, TimeSpan? pruneInterval = null)
    {
        Name = name;
        Identities<TallyTicked>(x => x.Tallies);
        Identities<TallyPoisoned>(x => x.Tallies);

        var index = retention == null ? Options.UseSliceIndex() : Options.UseSliceIndex(retention);
        if (pruneInterval.HasValue) index.PruneInterval = pruneInterval.Value;
    }

    public void Apply(TallyTicked e, ComplianceBalance tally) => tally.Amount++;

    public void Apply(TallyPoisoned e, ComplianceBalance tally)
    {
        TallyPoison.ThrowIfPoisoned(tally.Id);
        tally.Amount++;
    }

    public override ValueTask RaiseSideEffects(ComplianceOperations operations, IEventSlice<ComplianceBalance> slice)
        => TallyPoison.RaiseFor(slice);
}

/// <summary>
/// Class B with a lookup grouper: points are earned on a card, and the grouper reads which member owns the card. With
/// the slice index a replay gives a member the events it got when they were applied (as recorded), unless the
/// projection opts out and re-runs the grouper.
/// </summary>
public partial class ParkingLoyaltyProjection : ComplianceMemberLoyaltyProjectionBase
{
    public const string ProjectionName = "ParkingLoyalty";

    public ParkingLoyaltyProjection() : this(false)
    {
    }

    public ParkingLoyaltyProjection(bool rerunGrouping)
    {
        Name = ProjectionName;
        CustomGrouping(new Grouper());

        var index = Options.UseSliceIndex();
        if (rerunGrouping) index.RerunCustomGroupingOnReplay();
    }

    public void Apply(ParkingPointsEarned e, ComplianceMemberLoyalty member)
    {
        TallyPoison.ThrowIfPoisoned(member.Id);
        member.Points += e.Points;
    }

    public class Grouper : IJasperFxAggregateGrouper<Guid, ComplianceQuerySession>
    {
        public async Task Group(ComplianceQuerySession session, IReadOnlyList<IEvent> events,
            IEventGrouping<Guid> grouping)
        {
            foreach (var e in events.OfType<IEvent<ParkingPointsEarned>>())
            {
                var owner = await session.LoadAsync<ComplianceCardOwner>(e.Data.CardId).ConfigureAwait(false);
                if (owner != null)
                {
                    grouping.AddEvent(owner.MemberId, e);
                }
            }
        }
    }
}

/// <summary>
/// A lookup grouper with a fan-out before grouping: the index records the parent event, and only the grouper knows which
/// child went where, so even with the slice index this projection can only be scanned.
/// </summary>
public partial class ParkingLookupFanOutProjection : ComplianceMemberLoyaltyProjectionBase
{
    public ParkingLookupFanOutProjection()
    {
        Name = "ParkingLookupFanOut";
        CustomGrouping(new ParkingLoyaltyProjection.Grouper());
        FanOut<TallyTicked, ParkingPointsEarned>(x => x.Tallies.Select(t => new ParkingPointsEarned(t, 1)),
            FanoutMode.BeforeGrouping);
        Options.UseSliceIndex();
    }

    public void Apply(ParkingPointsEarned e, ComplianceMemberLoyalty member) => member.Points += e.Points;
}

#endregion

/// <summary>
/// Parking a failing identity of a multi-stream projection (marten#6003, phase 2). A multi-stream projection parks
/// when one identity's events can be found exactly: grouped by stream (<c>IdentityFromStream</c>), by DCB tag
/// (<c>IdentityFromTag</c>), or through its slice index (<c>UseSliceIndex()</c>). A replay or discard resumes from the
/// identity's stored document over its events since the park; a per-identity rebuild re-folds it from nothing. A
/// projection that can only be scanned pauses instead, and can still rebuild one identity by scanning.
/// </summary>
/// <remarks>
/// <para>
/// The tallies count: every event applied to a tally adds one, so a lost event and a doubled one both show, and every
/// applied event raises one side effect on the tally's audit stream, so "exactly once" is a count of distinct sequences.
/// </para>
/// <para>
/// The matrix: park, hold, replay, replay that fails again, discard, side effects, per-identity rebuild and reading an
/// identity's events over each exact class (stream, tag, slice index kept for parking, slice index kept in full); an
/// event fanned out to two identities where only one fails; the cap; a restart with a durable request; a lookup grouper
/// replayed as recorded and re-run; a slice index started over existing data, its retention, and a full rebuild
/// rewriting it; and a projection that can only be scanned, rebuilt by scan.
/// </para>
/// </remarks>
[RequiresCapability("SupportsParkedIdentities")]
public abstract class MultiStreamParkedIdentityCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static void addEvents(ComplianceStoreConfig config)
    {
        config.AddEventType<TallyTicked>();
        config.AddEventType<TallyPoisoned>();
        config.AddEventType<TallySideEffect>();
    }

    private static Action<ComplianceStoreConfig> configure(string schema, Func<ProjectionBase> projection,
        bool tags = false)
        => config =>
        {
            config.SchemaName = schema;
            addEvents(config);
            if (tags) config.RegisterTagType<TallyTag>("tally");
            config.AddProjection(projection(), ProjectionLifecycle.Async);
        };

    private static readonly Action<ComplianceStoreConfig> _loyaltyConfiguration = loyalty(false);

    private static Action<ComplianceStoreConfig> loyalty(bool rerun) => config =>
    {
        config.SchemaName = rerun ? "compliance_parking_loyalty_rerun" : "compliance_parking_loyalty";
        config.AddEventType<ParkingPointsEarned>();
        config.AddProjection(new ParkingLoyaltyProjection(rerun), ProjectionLifecycle.Async);
    };

    private static readonly Action<ComplianceStoreConfig> _scanOnlyConfiguration = config =>
    {
        config.SchemaName = "compliance_parking_scan_only";
        config.AddEventType<EmployeeHired>();
        config.AddEventType<DepartmentPoisoned>();
        config.AddProjection(new ParkingDepartmentProjection(), ProjectionLifecycle.Async);
    };

    /// <summary>The exact classes of the matrix.</summary>
    public static TheoryData<string> Kinds => new() { "stream", "tag", "index", "index-full" };

    /// <summary>The classes where one event can go to two identities.</summary>
    public static TheoryData<string> FanOutKinds => new() { "tag", "index", "index-full" };

    private sealed record Kind(string ProjectionName, Action<ComplianceStoreConfig> Configuration,
        IdentityEventSource Source);

    private static readonly Dictionary<string, Kind> _kinds = new()
    {
        ["stream"] = new Kind(ParkingStreamTallyProjection.ProjectionName,
            configure("compliance_parking_stream_tally", () => new ParkingStreamTallyProjection()),
            IdentityEventSource.Stream),
        ["tag"] = new Kind(ParkingTagTallyProjection.ProjectionName,
            configure("compliance_parking_tag_tally", () => new ParkingTagTallyProjection(), tags: true),
            IdentityEventSource.Tag),
        ["index"] = new Kind(ParkingIndexedTallyProjection.ProjectionName,
            configure("compliance_parking_indexed_tally", () => new ParkingIndexedTallyProjection()),
            IdentityEventSource.SliceIndex),
        ["index-full"] = new Kind("ParkingFullIndexedTally",
            configure("compliance_parking_full_indexed_tally",
                () => new ParkingIndexedTallyProjection("ParkingFullIndexedTally", SliceIndexRetention.Full)),
            IdentityEventSource.SliceIndex)
    };

    protected override Action<ComplianceStoreConfig> Configuration => _kinds["index"].Configuration;

    private IEventStore<TOperations, TQuerySession> Store => (IEventStore<TOperations, TQuerySession>)EventStore;

    private string _kind = "index";
    private readonly Dictionary<Guid, int> _expected = new();
    private readonly HashSet<Guid> _streams = new();
    private readonly List<Guid> _poisoned = new();

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsParkedIdentities,
            "This event store does not implement the parked-identity seams (marten#6003)");
    }

    private async Task useAsync(string kind)
    {
        _kind = kind;
        await theFixture.ConfigureAsync(_kinds[kind].Configuration);
        await theFixture.CleanEventDataAsync();

        Assert.SkipUnless(Store.SupportsIdentityEventSource(_kinds[kind].Source),
            $"This event store can't read one identity's events from the {_kinds[kind].Source} source");
    }

    private async Task reconfigureAsync(Action<ComplianceStoreConfig> configuration)
    {
        await theFixture.ConfigureAsync(configuration);
        await theFixture.CleanEventDataAsync();
    }

    private string projectionName => _kinds[_kind].ProjectionName;

    private void park(Action<ErrorHandlingOptions>? alter = null)
    {
        Store.ContinuousErrors.ApplyErrors = ProjectionErrorAction.Park;
        alter?.Invoke(Store.ContinuousErrors);
    }

    private void poison(Guid tally)
    {
        TallyPoison.On(tally);
        _poisoned.Add(tally);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var tally in _poisoned) TallyPoison.Off(tally);
        await base.DisposeAsync();
    }

    private async Task<IEventDatabase> theDatabaseAsync() => (await EventStore.AllDatabases()).First();

    private ShardName shardFor(string name) => EventStore.RegisteredShardNames().Single(x => x.Name == name);

    // One event for these tallies, the way the current kind finds them: on the tally's own stream, tagged, or named in
    // the body
    private async Task appendAsync(object body, params Guid[] tallies)
    {
        await using var session = OpenSession();
        var events = EventsFor(session);

        switch (_kind)
        {
            case "stream":
                tallies.Length.ShouldBe(1, "a stream-grouped tally gets its own stream's events only");
                if (_streams.Add(tallies[0]))
                {
                    events.StartStream(tallies[0], body);
                }
                else
                {
                    events.Append(tallies[0], body);
                }

                break;

            case "tag":
                var tagged = events.BuildEvent(body);
                tagged.WithTag(tallies.Select(x => (object)new TallyTag(x)).ToArray());
                events.Append(Guid.NewGuid(), tagged);
                break;

            default:
                events.StartStream(Guid.NewGuid(), body);
                break;
        }

        await SaveChangesAsync(session);

        foreach (var tally in tallies) _expected[tally] = _expected.GetValueOrDefault(tally) + 1;
    }

    private Task tickAsync(params Guid[] tallies) => appendAsync(new TallyTicked(tallies), tallies);

    private Task poisonedAsync(params Guid[] tallies) => appendAsync(new TallyPoisoned(tallies), tallies);

    private async Task<ComplianceBalance?> loadAsync(Guid tally)
    {
        await using var session = OpenSession();
        return await LoadDocumentAsync<ComplianceBalance>(session, tally);
    }

    private async Task<int> ticksOfAsync(Guid tally) => (await loadAsync(tally))?.Amount ?? 0;

    private async Task theTallyHasEverythingAsync(Guid tally, int missing = 0)
        => (await ticksOfAsync(tally)).ShouldBe(_expected[tally] - missing, $"tally {tally}: lost or doubled events");

    // The side effects committed for a tally: one per event it applied, exactly once
    private async Task<List<long>> sideEffectsForAsync(Guid tally)
    {
        await using var session = OpenSession();
        var audit = await EventsFor(session).FetchStreamAsync(TallyPoison.AuditStreamFor(tally), token: Cancellation);
        return audit.Select(x => x.Data).OfType<TallySideEffect>().Select(x => x.Sequence).OrderBy(x => x).ToList();
    }

    private async Task<IReadOnlyList<ParkedIdentity>> parkedRowsAsync(string? name = null)
        => await (await theDatabaseAsync()).FetchParkedIdentitiesAsync(
            new ParkedIdentityQuery { ProjectionName = name ?? projectionName }, Cancellation);

    // Two tallies whose first batch commits, then a poisoned event for the first and a tick for each
    private async Task<(IProjectionDaemon Daemon, Guid Parked, Guid Bystander)> aParkedTallyAsync(string kind,
        Action<ErrorHandlingOptions>? alter = null)
    {
        await useAsync(kind);
        park(alter);

        var parked = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        await tickAsync(parked);
        await tickAsync(parked);
        await tickAsync(bystander);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await ticksOfAsync(parked)).ShouldBe(2);

        poison(parked);
        await poisonedAsync(parked);
        await tickAsync(parked);
        await tickAsync(bystander);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var row = (await parkedRowsAsync()).ShouldHaveSingleItem();
        row.IdentityText.ShouldBe(ParkedIdentityText.Format(parked));
        row.Category.ShouldBe(ShardFailureCategory.ApplyEvent);

        return (daemon, parked, bystander);
    }

    // ---- every exact class

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task a_failing_identity_parks_holds_its_events_and_a_replay_unparks_it(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTallyAsync(kind);

        daemon.IsIdentityParked(projectionName, null, parked).ShouldBeTrue();

        // Frozen at the last good state; everyone else carried on
        (await ticksOfAsync(parked)).ShouldBe(2);
        await theTallyHasEverythingAsync(bystander);

        // Held while parked
        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await ticksOfAsync(parked)).ShouldBe(2);

        TallyPoison.Off(parked);
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);

        await theTallyHasEverythingAsync(parked);
        (await parkedRowsAsync()).ShouldBeEmpty();
        daemon.IsIdentityParked(projectionName, null, parked).ShouldBeFalse();

        // And it is live again
        await tickAsync(parked);
        await tickAsync(bystander);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await theTallyHasEverythingAsync(parked);
        await theTallyHasEverythingAsync(bystander);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task a_replay_that_fails_again_leaves_the_identity_parked_with_the_new_error(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTallyAsync(kind);

        var result = await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation);
        result.Outcome.ShouldBe(ParkedReplayOutcome.FailedAgain);
        result.Exception.ShouldNotBeNull();

        // Written out of band, so give it a moment
        var row = await eventuallyAsync(async () =>
        {
            var current = (await parkedRowsAsync()).ShouldHaveSingleItem();
            return current.ReplayAttempts > 0 ? current : null;
        });

        row.ReplayAttempts.ShouldBe(1);
        row.LastAttemptError.ShouldNotBeNull().ShouldContain("poisoned");
        (await ticksOfAsync(parked)).ShouldBe(2);
        daemon.IsIdentityParked(projectionName, null, parked).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task a_discard_dead_letters_what_still_fails_applies_the_rest_and_unparks(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, _) = await aParkedTallyAsync(kind);

        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await daemon.DiscardParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Discarded);

        // Everything but the poisoned event
        await theTallyHasEverythingAsync(parked, missing: 1);
        (await parkedRowsAsync()).ShouldBeEmpty();

        var deadLetters = await eventuallyAsync(async () =>
        {
            var letters = await (await theDatabaseAsync()).QueryDeadLetterEventsAsync(shardFor(projectionName), null,
                0, 100, Cancellation);
            return letters.Count > 0 ? letters : null;
        });

        deadLetters.ShouldHaveSingleItem();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task side_effects_of_the_held_events_fire_exactly_once_on_replay(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTallyAsync(kind);

        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        // Only the two events applied before the park
        (await sideEffectsForAsync(parked)).Count.ShouldBe(2);

        // A replay that fails first fires nothing
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.FailedAgain);
        (await sideEffectsForAsync(parked)).Count.ShouldBe(2);

        TallyPoison.Off(parked);
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var fired = await sideEffectsForAsync(parked);
        fired.Count.ShouldBe(_expected[parked]);
        fired.Distinct().Count().ShouldBe(fired.Count, "a side effect fired twice");

        (await sideEffectsForAsync(bystander)).Count.ShouldBe(_expected[bystander]);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task a_per_identity_rebuild_of_a_parked_identity_unparks_it(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTallyAsync(kind);

        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        TallyPoison.Off(parked);

        var result = await daemon.RebuildIdentityAsync(projectionName, null, parked, Cancellation);

        result.Unparked.ShouldBeTrue();
        (await parkedRowsAsync()).ShouldBeEmpty();
        await theTallyHasEverythingAsync(parked);

        // An identity that isn't parked rebuilds from nothing to the same answer
        (await daemon.RebuildIdentityAsync(projectionName, null, bystander, Cancellation)).Unparked.ShouldBeFalse();
        await theTallyHasEverythingAsync(bystander);

        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await theTallyHasEverythingAsync(parked);

        // The held events fired their side effects once, through the rebuild
        var fired = await sideEffectsForAsync(parked);
        fired.Count.ShouldBe(_expected[parked]);
        fired.Distinct().Count().ShouldBe(fired.Count);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task one_identitys_events_are_read_exactly_from_its_source(string kind)
    {
        SkipUnlessSupported();
        await useAsync(kind);

        var tally = Guid.NewGuid();
        var other = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await tickAsync(tally);
            await tickAsync(other);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var database = await theDatabaseAsync();
        var page = await Store.LoadIdentityEventsAsync(database, new IdentityEventsRequest
        {
            ProjectionName = projectionName,
            Identity = tally,
            Limit = 3
        }, Cancellation);

        page.Source.ShouldBe(_kinds[kind].Source);
        page.Exact.ShouldBeTrue();
        page.Events.Count.ShouldBe(3);
        page.Truncated.ShouldBeTrue();

        // By text too, which is how a monitor names it
        var rest = await Store.LoadIdentityEventsAsync(database, new IdentityEventsRequest
        {
            ProjectionName = projectionName,
            Identity = tally.ToString(),
            AfterSequence = page.NextAfter,
            Limit = 100
        }, Cancellation);

        rest.Truncated.ShouldBeFalse();
        var all = page.Events.Concat(rest.Events).ToList();
        all.Count.ShouldBe(5);
        all.Select(x => x.Sequence).ShouldBeInOrder();
        all.ShouldAllBe(x => x.Data is TallyTicked);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task nothing_is_lost_or_applied_twice_across_park_and_replay_during_appends(string kind)
    {
        SkipUnlessSupported();
        var (daemon, parked, bystander) = await aParkedTallyAsync(kind);

        // Appending until the replay is done, so the batch the replay binds to carries the identity's events too
        using var stop = new CancellationTokenSource();
        var appender = appendUntilAsync(stop.Token, parked, bystander);

        await Task.Delay(50, Cancellation);
        TallyPoison.Off(parked);
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);

        await stop.CancelAsync();
        await appender;
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await theTallyHasEverythingAsync(parked);
        await theTallyHasEverythingAsync(bystander);

        var fired = await sideEffectsForAsync(parked);
        fired.Count.ShouldBe(_expected[parked]);
        fired.Distinct().Count().ShouldBe(fired.Count, "a side effect fired twice");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task rebuilding_one_identity_during_appends_loses_and_doubles_nothing(string kind)
    {
        SkipUnlessSupported();
        await useAsync(kind);

        var tally = Guid.NewGuid();
        for (var i = 0; i < 5; i++) await tickAsync(tally);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        using var stop = new CancellationTokenSource();
        var appender = appendUntilAsync(stop.Token, tally);

        await Task.Delay(50, Cancellation);
        (await daemon.RebuildIdentityAsync(projectionName, null, tally, Cancellation)).Action
            .ShouldBe(ActionType.Store);

        await stop.CancelAsync();
        await appender;
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await theTallyHasEverythingAsync(tally);
    }

    // One event per tally, over and over, until stopped and at least ten times
    private Task appendUntilAsync(CancellationToken stop, params Guid[] tallies) => Task.Run(async () =>
    {
        for (var i = 0; i < 10 || !stop.IsCancellationRequested; i++)
        {
            foreach (var tally in tallies) await tickAsync(tally);
            await Task.Delay(2, Cancellation);
        }
    }, Cancellation);

    // ---- one event, two identities

    [Theory]
    [MemberData(nameof(FanOutKinds))]
    public async Task an_event_for_two_identities_parks_only_the_one_that_fails(string kind)
    {
        SkipUnlessSupported();
        await useAsync(kind);
        park();

        var failing = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        await tickAsync(failing, sibling);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        poison(failing);
        await poisonedAsync(failing, sibling);
        await tickAsync(failing, sibling);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var row = (await parkedRowsAsync()).ShouldHaveSingleItem();
        row.IdentityText.ShouldBe(ParkedIdentityText.Format(failing));

        // The sibling applied the same event
        await theTallyHasEverythingAsync(sibling);
        (await ticksOfAsync(failing)).ShouldBe(1);

        TallyPoison.Off(failing);
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, failing, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);

        await theTallyHasEverythingAsync(failing);
        await theTallyHasEverythingAsync(sibling);
        (await sideEffectsForAsync(sibling)).Count.ShouldBe(_expected[sibling]);
        (await sideEffectsForAsync(failing)).Count.ShouldBe(_expected[failing]);
    }

    // ---- guards and lifecycle

    [Fact]
    public async Task passing_the_cap_pauses_the_shard_and_keeps_the_parks()
    {
        SkipUnlessSupported();
        var (daemon, first, _) = await aParkedTallyAsync("index", x => x.MaxParkedIdentitiesPerShard = 1);
        var shard = shardFor(projectionName);

        var second = Guid.NewGuid();
        await tickAsync(second);
        await WaitForNonStaleProjectionDataAsync(_timeout);

        poison(second);
        await poisonedAsync(second);

        await eventuallyAsync(() =>
            Task.FromResult(daemon.StatusFor(shard.Identity) == AgentStatus.Paused ? "paused" : null));

        daemon.CurrentAgents().Single(x => x.Name.Identity == shard.Identity).Failure.ShouldNotBeNull()
            .Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);

        (await parkedRowsAsync()).ShouldHaveSingleItem().IdentityText.ShouldBe(ParkedIdentityText.Format(first));
    }

    [Fact]
    public async Task parked_identities_and_a_pending_request_survive_a_restart()
    {
        SkipUnlessSupported();
        var (_, parked, _) = await aParkedTallyAsync("index");

        await (await theDatabaseAsync()).RequestParkedActionAsync(new ParkedActionRequest
        {
            ProjectionName = projectionName,
            Action = ParkedAction.Replay
        }, Cancellation);

        await StopDaemonAsync();
        (await parkedRowsAsync()).ShouldHaveSingleItem().RequestedAction.ShouldBe(ParkedAction.Replay);

        TallyPoison.Off(parked);
        await tickAsync(parked);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        // Loaded at start: still held
        daemon.IsIdentityParked(projectionName, null, parked).ShouldBeTrue();
        (await ticksOfAsync(parked)).ShouldBe(2);

        // The heartbeat runs every ten seconds
        await eventuallyAsync(async () => (await parkedRowsAsync()).Count == 0 ? "gone" : null,
            TimeSpan.FromSeconds(45));

        await theTallyHasEverythingAsync(parked);
    }

    // ---- the slice index

    [Fact]
    public async Task an_index_that_starts_after_existing_data_parks_and_replays_but_cannot_rebuild_from_nothing()
    {
        SkipUnlessSupported();
        await useAsync("index-full");
        park();

        var database = await theDatabaseAsync();
        var parked = Guid.NewGuid();
        await tickAsync(parked);
        await tickAsync(parked);

        // As if the index were turned on for a projection that already ran over these events
        var existing = await database.FetchHighestEventSequenceNumber(Cancellation);
        await database.SetEventRangeMarkAsync(SliceIndexMarks.KeyFor(projectionName, 1), null, existing, Cancellation);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        poison(parked);
        await poisonedAsync(parked);
        await tickAsync(parked);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await parkedRowsAsync()).ShouldHaveSingleItem();

        // A rebuild from nothing would miss the events before the index started
        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            daemon.RebuildIdentityAsync(projectionName, null, parked, Cancellation));
        refused.Reason.ShouldBe(IdentityRebuildRefusal.SliceIndexIncomplete);
        refused.Message.ShouldContain(existing.ToString());

        // Reading the identity's events from before the start is not exact
        (await Store.LoadIdentityEventsAsync(database,
            new IdentityEventsRequest { ProjectionName = projectionName, Identity = parked }, Cancellation))
            .Exact.ShouldBeFalse();

        // A replay only needs the rows after the park, which the index has
        TallyPoison.Off(parked);
        (await daemon.ReplayParkedIdentityAsync(projectionName, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);
        await theTallyHasEverythingAsync(parked);

        // A full rebuild writes the whole index, and then one identity can be rebuilt from nothing
        await daemon.RebuildProjectionAsync(projectionName, _timeout, Cancellation);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await database.FetchEventRangeMarkAsync(SliceIndexMarks.KeyFor(projectionName, 1), null, Cancellation))
            .ShouldBe(0);

        var reloaded = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await reloaded.RebuildIdentityAsync(projectionName, null, parked, Cancellation)).Action
            .ShouldBe(ActionType.Store);
        await theTallyHasEverythingAsync(parked);

        var page = await Store.LoadIdentityEventsAsync(database,
            new IdentityEventsRequest { ProjectionName = projectionName, Identity = parked }, Cancellation);
        page.Exact.ShouldBeTrue();
        page.Events.Count.ShouldBe(_expected[parked]);
    }

    [Fact]
    public async Task pruning_the_index_keeps_what_a_parked_identity_needs()
    {
        SkipUnlessSupported();
        await reconfigureAsync(configure("compliance_parking_pruned_tally",
            () => new ParkingIndexedTallyProjection("ParkingPrunedTally",
                SliceIndexRetention.ForParking(TimeSpan.FromMilliseconds(1)), TimeSpan.Zero)));
        _kind = "index";
        Assert.SkipUnless(Store.SupportsIdentityEventSource(IdentityEventSource.SliceIndex),
            "This event store does not keep slice indexes");
        park();

        const string name = "ParkingPrunedTally";
        var parked = Guid.NewGuid();
        var other = Guid.NewGuid();
        await tickAsync(parked);
        await tickAsync(other);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        poison(parked);
        await poisonedAsync(parked);
        await tickAsync(parked);
        await tickAsync(other);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await parkedRowsAsync(name)).ShouldHaveSingleItem();

        var database = await theDatabaseAsync();
        var key = SliceIndexMarks.KeyFor(name, 1);

        // The prune runs on the shard's heartbeat, every ten seconds, and moves the index's start up
        await eventuallyAsync(async () =>
            await database.FetchEventRangeMarkAsync(key, null, Cancellation) is > 0 ? "pruned" : null,
            TimeSpan.FromSeconds(45));

        // Nothing at or after the park's resume point went
        var row = (await parkedRowsAsync(name)).Single();
        (await database.FetchEventRangeMarkAsync(key, null, Cancellation)).ShouldNotBeNull()
            .ShouldBeLessThanOrEqualTo(row.ResumeAfter);

        TallyPoison.Off(parked);
        (await daemon.ReplayParkedIdentityAsync(name, null, parked, Cancellation)).Outcome
            .ShouldBe(ParkedReplayOutcome.Replayed);
        await theTallyHasEverythingAsync(parked);

        // But one identity can't be rebuilt from nothing any more
        (await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            daemon.RebuildIdentityAsync(name, null, other, Cancellation))).Reason
            .ShouldBe(IdentityRebuildRefusal.SliceIndexIncomplete);
    }

    // ---- lookup groupers under the slice index

    private async Task<(IProjectionDaemon Daemon, Guid Card, Guid First, Guid Second)> aParkedMemberAsync(bool rerun)
    {
        await reconfigureAsync(loyalty(rerun));
        Assert.SkipUnless(Store.SupportsIdentityEventSource(IdentityEventSource.SliceIndex),
            "This event store does not keep slice indexes");
        park();

        var card = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await ownAsync(card, first);
        await earnAsync(card, 1);

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await pointsOfAsync(first)).ShouldBe(1);

        poison(first);
        await earnAsync(card, 2);
        await WaitForNonStaleProjectionDataAsync(_timeout);
        (await parkedRowsAsync(ParkingLoyaltyProjection.ProjectionName)).ShouldHaveSingleItem().IdentityText
            .ShouldBe(ParkedIdentityText.Format(first));

        // The card changes hands after the event was applied
        await ownAsync(card, second);
        TallyPoison.Off(first);

        return (daemon, card, first, second);
    }

    private async Task ownAsync(Guid card, Guid member)
    {
        await using var session = OpenSession();
        StoreDocument(session, new ComplianceCardOwner { Id = card, MemberId = member });
        await SaveChangesAsync(session);
    }

    private async Task earnAsync(Guid card, int points)
    {
        await using var session = OpenSession();
        EventsFor(session).StartStream(Guid.NewGuid(), new ParkingPointsEarned(card, points));
        await SaveChangesAsync(session);
    }

    private async Task<int> pointsOfAsync(Guid member)
    {
        await using var session = OpenSession();
        return (await LoadDocumentAsync<ComplianceMemberLoyalty>(session, member))?.Points ?? 0;
    }

    [Fact]
    public async Task a_lookup_grouping_is_replayed_as_recorded_by_default()
    {
        SkipUnlessSupported();
        var (daemon, _, first, second) = await aParkedMemberAsync(rerun: false);

        (await daemon.ReplayParkedIdentityAsync(ParkingLoyaltyProjection.ProjectionName, null, first, Cancellation))
            .Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

        // The index says the member got the points, so the member gets them, whoever owns the card today
        (await pointsOfAsync(first)).ShouldBe(3);
        (await pointsOfAsync(second)).ShouldBe(0);
    }

    [Fact]
    public async Task a_lookup_grouping_runs_again_when_the_projection_opts_out()
    {
        SkipUnlessSupported();
        var (daemon, _, first, second) = await aParkedMemberAsync(rerun: true);

        (await daemon.ReplayParkedIdentityAsync(ParkingLoyaltyProjection.ProjectionName, null, first, Cancellation))
            .Outcome.ShouldBe(ParkedReplayOutcome.Replayed);

        // The grouping runs again over the indexed events, and today the card belongs to someone else. A replay only
        // writes the parked identity, so nobody gets the points.
        (await pointsOfAsync(first)).ShouldBe(1);
        (await pointsOfAsync(second)).ShouldBe(0);
        (await parkedRowsAsync(ParkingLoyaltyProjection.ProjectionName)).ShouldBeEmpty();
    }

    [Fact]
    public void each_projection_says_how_it_finds_one_identitys_events()
    {
        new ParkingStreamTallyProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.Stream);
        new ParkingTagTallyProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.Tag);
        new ParkingIndexedTallyProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.SliceIndex);
        new ParkingLoyaltyProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.SliceIndex);
        new ParkingDepartmentProjection().IdentityAccess.Class.ShouldBe(IdentityAccessClass.ScanOnly);

        var fannedOut = new ParkingLookupFanOutProjection().IdentityAccess;
        fannedOut.Class.ShouldBe(IdentityAccessClass.ScanOnly);
        fannedOut.CanPark.ShouldBeFalse();
        fannedOut.Reason.ShouldContain("fan-out before grouping");

        new ParkingLoyaltyProjection().IdentityAccess.Reason.ShouldContain("recorded");
        new ParkingLoyaltyProjection(true).IdentityAccess.Reason.ShouldContain("RerunCustomGroupingOnReplay");
        new ParkingDepartmentProjection().IdentityAccess.Reason.ShouldContain("UseSliceIndex()");
    }

    [Fact]
    public async Task describe_reports_that_the_projection_parks()
    {
        SkipUnlessSupported();
        await useAsync("tag");

        var usage = await EventStore.TryCreateUsage(Cancellation);
        var descriptor = usage.ShouldNotBeNull().Subscriptions.Single(x => x.Name == projectionName);
        descriptor.IdentityAccess.ShouldBe(IdentityAccessClass.Tag);
        descriptor.CanPark.ShouldBeTrue();
    }

    // ---- a projection that can only be scanned

    [Fact]
    public async Task a_projection_that_can_only_be_scanned_rebuilds_one_identity_by_scan()
    {
        SkipUnlessSupported();
        await reconfigureAsync(_scanOnlyConfiguration);
        Assert.SkipUnless(Store.SupportsIdentityEventSource(IdentityEventSource.Scan),
            "This event store can't scan for one identity's events");

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(Guid.NewGuid(), new EmployeeHired("scanning", "Ann"),
                new EmployeeHired("elsewhere", "Bob"));
            EventsFor(session).StartStream(Guid.NewGuid(), new EmployeeHired("scanning", "Cy"));
            await SaveChangesAsync(session);
        }

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        var name = ParkingDepartmentProjection.ProjectionName;

        // Its identity can't be found any other way
        (await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            daemon.RebuildIdentityAsync(name, null, "scanning", Cancellation))).Reason
            .ShouldBe(IdentityRebuildRefusal.IdentityNotFindable);

        var progress = new List<IdentityScanProgress>();
        var result = await daemon.RebuildIdentityByScanAsync(name, null, "scanning",
            new SynchronousProgress<IdentityScanProgress>(progress.Add), Cancellation);

        result.Action.ShouldBe(ActionType.Store);
        progress.ShouldNotBeEmpty();
        progress.Last().EventsFound.ShouldBe(2);

        await using (var session = OpenSession())
        {
            var department = await LoadDocumentAsync<ComplianceDepartment>(session, "scanning");
            department.ShouldNotBeNull().HeadCount.ShouldBe(2);
        }

        // Reading its events takes a window, and says the answer is not exact
        var database = await theDatabaseAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => Store.LoadIdentityEventsAsync(database,
            new IdentityEventsRequest { ProjectionName = name, Identity = "scanning" }, Cancellation));

        var page = await Store.LoadIdentityEventsAsync(database,
            new IdentityEventsRequest { ProjectionName = name, Identity = "scanning", ScanWindow = 1000 },
            Cancellation);
        page.Source.ShouldBe(IdentityEventSource.Scan);
        page.Exact.ShouldBeFalse();
        page.Events.Count.ShouldBe(2);
    }

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

    // Progress<T> posts to the thread pool, so its reports can arrive after the rebuild returns
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
