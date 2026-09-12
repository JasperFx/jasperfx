using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Projection status events

public record SignalRaised(string Flag);

public record SignalLowered(string Flag);

#endregion

/// <summary>
/// An <see cref="ProjectionLifecycle.Async" /> snapshot — the registration that actually runs a
/// daemon shard, and therefore the only one with a runtime state to report.
/// </summary>
public partial class SignalTally
{
    public Guid Id { get; set; }
    public int Raised { get; set; }
    public int Lowered { get; set; }

    public void Apply(SignalRaised e) => Raised++;

    public void Apply(SignalLowered e) => Lowered++;
}

/// <summary>
/// An <see cref="ProjectionLifecycle.Inline" /> snapshot, registered so the suite can hold the
/// no-shards case to a definition.
/// </summary>
/// <remarks>
/// An inline projection runs no daemon agent, so there is nothing whose runtime state could be
/// reported. Polecat synthesised a shard for it and put the lifecycle string in the
/// <see cref="ShardStatus.State" /> slot, which is what makes this a registration worth carrying
/// rather than an obvious case.
/// </remarks>
public partial class SignalBoard
{
    public Guid Id { get; set; }
    public string Last { get; set; } = string.Empty;

    public void Apply(SignalRaised e) => Last = e.Flag;

    public void Apply(SignalLowered e) => Last = string.Empty;
}

/// <summary>
/// <c>IEventStore.GetProjectionStatusesAsync</c> — the snapshot a monitoring console's projections
/// page renders before it subscribes to <c>ShardStatesChanged</c>. Pins what the five fields of a
/// <see cref="ShardStatus" /> mean (jasperfx#818).
/// </summary>
/// <remarks>
/// <para>
/// Marten, Polecat and Fisher all implement this and there was no shared suite for it, so each store
/// decided independently — and reasonably — what the same five fields meant, and arrived at three
/// different answers. A store-agnostic console reading all three got different semantics per store
/// with nothing announcing it. That is the jasperfx#700 / #718 / #732 pattern once more: a capability
/// every store is assumed to share, with nothing shared holding any of them to it.
/// </para>
/// <para>
/// <b>The two rulings this suite encodes</b>, neither of which is a vote of the current
/// implementations:
/// </para>
/// <para>
/// <b>1. <see cref="ShardStatus.State" /> is a fact about the running daemon.</b> A store that can
/// reach one reports what it says; a store that cannot reports
/// <see cref="ShardStatusState.Unknown" />, which means "there is no daemon here to ask" and is a
/// different operational situation from <see cref="ShardStatusState.Stopped" />. Only Fisher read it
/// that way. Marten answered <c>Unknown</c> unconditionally (never reading a daemon at all) and
/// Polecat hardcoded <c>Stopped</c> — which is not a partial answer but a wrong one, indistinguishable
/// from a daemon that really has stopped, and it is the reading an operator acts on. Both stores
/// change: marten#5383 and polecat#589.
/// </para>
/// <para>
/// <b>2. The inventory is projections, not shards.</b> <c>Projections.All</c>, so subscriptions are
/// not in it — Marten's and Polecat's reading, and Fisher is the store that changes (fisher#243).
/// A subscription genuinely is a daemon shard with progress to report, which is why this needed a
/// ruling rather than a test, but this is the page an operator opens to ask about read models and a
/// subscription has no document behind it. Subscription progress stays reachable non-generically
/// through <c>RegisteredShardNames()</c> against <c>FetchProjectionLagAsync</c> (jasperfx#815).
/// </para>
/// <para>
/// <b>Where each fact is asserted matters.</b> The fixture's own store is built by hand and has no
/// coordinator, which makes it a genuine instance of "no daemon visible" rather than a contrivance —
/// so the <c>Unknown</c> facts run against it. The one fact that needs a reachable running daemon
/// runs against <see cref="IComplianceCoordinatorHost{TOperations}" />, the store registered the
/// documented way, because that is the only store in the suite set where a daemon is genuinely
/// discoverable.
/// </para>
/// </remarks>
public abstract class ProjectionStatusCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly ComplianceSubscription _subscription = new();

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_projection_status";

        config.AddEventType<SignalRaised>();
        config.AddEventType<SignalLowered>();

        config.Snapshot<SignalTally>(SnapshotLifecycle.Async);
        config.Snapshot<SignalBoard>(SnapshotLifecycle.Inline);

        // Registered only so the inventory ruling is observable. Nothing here starts it or asserts on
        // what it received -- that is SubscriptionCompliance's job.
        config.Subscribe(_subscription);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gated on the explorer flag rather than a new one: this read is the projections page of the
    /// same explorer surface, and a store implementing one without the other has never happened.
    /// </summary>
    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsExplorerSurface,
            "This event store does not implement the event store explorer surface.");
    }

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    private async Task<Guid> aSignalAsync(params object[] extra)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new SignalRaised("Blue Peter") }.Concat(extra).ToArray();
        EventsFor(session).StartStream<SignalTally>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<IReadOnlyList<ProjectionStatus>> theStatusesAsync()
        => await EventStore.GetProjectionStatusesAsync(Cancellation);

    private static ProjectionStatus statusFor(IReadOnlyList<ProjectionStatus> statuses, string name)
    {
        var status = statuses.SingleOrDefault(x => x.ProjectionName == name);

        status.ShouldNotBeNull(
            $"No ProjectionStatus was reported for the registered projection '{name}'. Reported: " +
            (statuses.Count == 0 ? "(nothing)" : string.Join(", ", statuses.Select(x => x.ProjectionName))));

        return status;
    }

    /// <summary>
    /// The inventory is the registered projections, and an async registration carries the shards it
    /// runs as.
    /// </summary>
    /// <remarks>
    /// The shard name is asserted as the compound <c>ShardName.Identity</c> form
    /// (<c>SignalTally:All</c>) rather than merely non-empty: it is the key a console joins against
    /// the progression rows and against <c>ShardStatesChanged</c>, so a store reporting the bare
    /// projection name here produces a page whose rows never match the live updates.
    /// </remarks>
    [Fact]
    public async Task an_async_projection_is_reported_with_its_shards()
    {
        SkipUnlessSupported();

        var statuses = await theStatusesAsync();

        var tally = statusFor(statuses, nameof(SignalTally));

        tally.Lifecycle.ShouldBe(nameof(ProjectionLifecycle.Async));
        tally.Shards.ShouldNotBeEmpty(
            "An Async projection was reported with no shards, so its row on a projections page has no progress to show.");

        foreach (var shard in tally.Shards)
        {
            shard.ShardName.ShouldStartWith(nameof(SignalTally));
            shard.ShardName.ShouldContain(":",
                Case.Sensitive,
                "ShardStatus.ShardName must be the compound ShardName.Identity form (projection name and group key), which is the key a console joins against progression rows and live shard states.");
        }
    }

    /// <summary>
    /// A projection that runs no async shards reports an empty shard list, and its lifecycle is the
    /// thing that explains why.
    /// </summary>
    /// <remarks>
    /// The second assertion is the load-bearing one. Polecat synthesised a shard for an Inline
    /// registration and put <c>Lifecycle.ToString()</c> in the <see cref="ShardStatus.State" /> slot,
    /// so that one field meant a daemon state on some rows and a lifecycle on others — and a console
    /// filtering "show me everything that isn't Running" surfaced every inline projection in the
    /// store as though something were wrong with it.
    /// </remarks>
    [Fact]
    public async Task an_inline_projection_reports_no_shards_and_its_lifecycle()
    {
        SkipUnlessSupported();

        var statuses = await theStatusesAsync();

        var board = statusFor(statuses, nameof(SignalBoard));

        board.Lifecycle.ShouldBe(nameof(ProjectionLifecycle.Inline));
        board.Shards.ShouldBeEmpty(
            "An Inline projection runs no daemon agent, so it has no shard whose runtime state could be reported. ProjectionStatus.Lifecycle already says why the list is empty.");
    }

    /// <summary>
    /// <see cref="ShardStatus.State" /> is drawn from a closed vocabulary — never a lifecycle, never
    /// a store's own private wording.
    /// </summary>
    [Fact]
    public async Task every_reported_state_is_in_the_shared_vocabulary()
    {
        SkipUnlessSupported();

        var statuses = await theStatusesAsync();

        foreach (var shard in statuses.SelectMany(x => x.Shards))
        {
            ShardStatusState.All.ShouldContain(shard.State,
                $"Shard '{shard.ShardName}' reported a State of '{shard.State}', which is not in the ShardStatusState vocabulary. A console renders this string and filters on it.");
        }
    }

    /// <summary>
    /// <b>The sharpest fact.</b> With no daemon visible to the store, the answer is
    /// <see cref="ShardStatusState.Unknown" /> — not <see cref="ShardStatusState.Stopped" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture's store is built by hand and registers no coordinator, which is a real instance of
    /// the case rather than a contrivance: <c>DaemonMode.ExternallyManaged</c>, a monitoring console
    /// in another process, and a hand-built store all reach the same place. "There is no daemon here
    /// to ask" and "a daemon reports this shard as stopped" are different operational situations, and
    /// answering the second for the first is the reading an operator acts on — they go looking for
    /// why a projection was stopped that was never stopped.
    /// </para>
    /// <para>
    /// Asserted against every shard rather than one, because a store that hardcodes an answer
    /// hardcodes it everywhere.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task with_no_daemon_visible_the_state_is_unknown_rather_than_stopped()
    {
        SkipUnlessSupported();

        await aSignalAsync(new SignalLowered("Blue Peter"));

        var statuses = await theStatusesAsync();

        var shards = statuses.SelectMany(x => x.Shards).ToList();
        shards.ShouldNotBeEmpty();

        foreach (var shard in shards)
        {
            shard.State.ShouldBe(ShardStatusState.Unknown,
                $"Shard '{shard.ShardName}' reported '{shard.State}' with no daemon reachable. Only a daemon can report Stopped; the absence of one is Unknown.");
        }
    }

    /// <summary>
    /// Reading the statuses is a diagnostics read and does not change what is running.
    /// </summary>
    /// <remarks>
    /// Worth pinning because the obvious way to fill in <see cref="ShardStatus.State" /> is to ask
    /// the store for a daemon, and the obvious member to ask — <c>DaemonForDatabase</c> — is
    /// contractually allowed to go looking and <em>start</em> one. A projections page that starts a
    /// daemon by being opened is a monitoring tool with a side effect on the system it monitors.
    /// <c>IProjectionCoordinator.AllDaemonsAsync()</c> is the member that only observes.
    /// </remarks>
    [Fact]
    public async Task reading_the_statuses_does_not_start_a_daemon()
    {
        SkipUnlessSupported();

        await aSignalAsync(new SignalLowered("Blue Peter"));

        await theStatusesAsync();

        // If the first read started a daemon, the second sees it -- either as a running state or as
        // progress the first read reported none of.
        var second = await theStatusesAsync();

        foreach (var shard in second.SelectMany(x => x.Shards))
        {
            shard.State.ShouldBe(ShardStatusState.Unknown,
                $"Shard '{shard.ShardName}' reported '{shard.State}' on a second read, so reading the statuses started a daemon.");
            shard.ProcessedSequence.ShouldBe(0,
                $"Shard '{shard.ShardName}' had advanced to {shard.ProcessedSequence} without any daemon having been started.");
        }
    }

    /// <summary>
    /// <see cref="ShardStatus.EventStoreSequence" /> is the head of the event store, not the daemon's
    /// progress.
    /// </summary>
    /// <remarks>
    /// The gap between it and <see cref="ShardStatus.ProcessedSequence" /> is exactly what a console
    /// renders as lag. The high-water progression row records where the daemon got to, so a daemon
    /// that is not running leaves it behind — and sourcing this field from that row makes every shard
    /// on a stopped daemon report zero lag. That is the opposite of what a projections page is opened
    /// to find out, and it is the answer a store gives at the moment the answer matters most.
    /// </remarks>
    [Fact]
    public async Task the_event_store_sequence_is_the_head_of_the_store_not_the_daemons_progress()
    {
        SkipUnlessSupported();

        await aSignalAsync(new SignalLowered("Blue Peter"), new SignalRaised("Bravo"));

        var statuses = await theStatusesAsync();

        var shards = statuses.SelectMany(x => x.Shards).ToList();
        shards.ShouldNotBeEmpty();

        foreach (var shard in shards)
        {
            // Three events appended, no daemon ever started: the store's head has moved and the
            // shard's own progression has not.
            shard.EventStoreSequence.ShouldBeGreaterThanOrEqualTo(3,
                $"Shard '{shard.ShardName}' reported an EventStoreSequence of {shard.EventStoreSequence} after three events were appended with no daemon running. This field is the head of the event store, not the high-water row.");
            shard.ProcessedSequence.ShouldBe(0);
        }
    }

    /// <summary>
    /// <see cref="ShardStatus.Error" /> is null until something fails — a latched error, not a slot
    /// filled with whatever was to hand.
    /// </summary>
    [Fact]
    public async Task no_error_is_latched_when_nothing_has_failed()
    {
        SkipUnlessSupported();

        await aSignalAsync();

        var statuses = await theStatusesAsync();

        foreach (var shard in statuses.SelectMany(x => x.Shards))
        {
            shard.Error.ShouldBeNull(
                $"Shard '{shard.ShardName}' latched an error ('{shard.Error}') on a store where nothing has failed.");
        }
    }

    /// <summary>
    /// <b>The inventory ruling.</b> A registered subscription is not a projection and is not in this
    /// list.
    /// </summary>
    /// <remarks>
    /// The suite registers a real subscription, so a store answering from <c>AllShards()</c> reports
    /// it here and fails. The argument for the other reading is real — a subscription is a daemon
    /// shard with progress worth watching — and this is deliberately not where it is answered: a
    /// consumer that wants every shard's progress asks <c>RegisteredShardNames()</c> and correlates
    /// against <c>FetchProjectionLagAsync</c>, which is the pairing jasperfx#815 built for exactly
    /// that question.
    /// </remarks>
    [Fact]
    public async Task subscriptions_are_not_in_the_projection_inventory()
    {
        SkipUnlessSupported();

        var statuses = await theStatusesAsync();

        statuses.Select(x => x.ProjectionName)
            .ShouldNotContain(ComplianceSubscription.SubscriptionName,
                "GetProjectionStatusesAsync reports the registered projections (Projections.All). A subscription is a daemon shard, not a projection, and appears here only if the store answered from AllShards().");

        statuses.SelectMany(x => x.Shards).Select(x => x.ShardName)
            .ShouldNotContain(x => x.StartsWith(ComplianceSubscription.SubscriptionName, StringComparison.Ordinal),
                "A subscription's shard was reported inside a ProjectionStatus.");
    }

    /// <summary>
    /// <b>The other half of the State ruling.</b> Where a daemon genuinely is reachable, the reported
    /// state is the one that daemon is in — so <see cref="ShardStatusState.Unknown" /> means the
    /// absence of a daemon and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run against <see cref="IComplianceCoordinatorHost{TOperations}" /> rather than the fixture's
    /// own store, because a hand-built daemon is precisely the case <c>Unknown</c> describes and this
    /// is the only store in the suite set with a discoverable one. It is also the pairing that makes
    /// the ruling testable at all: without this fact a store could satisfy every assertion above by
    /// hardcoding <c>Unknown</c>, which is what Marten did.
    /// </para>
    /// <para>
    /// Deliberately paired with the progression assertion. A shard reported as <c>Running</c> whose
    /// <see cref="ShardStatus.ProcessedSequence" /> is still zero after the daemon has caught up is
    /// a state string from a registry rather than from a daemon.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_reachable_running_daemon_reports_the_real_shard_state()
    {
        SkipUnlessSupported();
        SkipUnlessDaemonIsSupported();

        await using var host = await StartCoordinatorHostAsync();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var streamId = Guid.NewGuid();
        await using (var session = host.OpenSession())
        {
            EventsFor(session).StartStream<SignalTally>(streamId, new SignalRaised("Blue Peter"),
                new SignalLowered("Blue Peter"));
            await SaveChangesAsync(session);
        }

        await coordinator.DaemonForMainDatabase().WaitForNonStaleData(_timeout);

        var statuses = await host.EventStore.GetProjectionStatusesAsync(Cancellation);

        var tally = statusFor(statuses, nameof(SignalTally));
        var shard = tally.Shards.ShouldHaveSingleItem();

        shard.State.ShouldBe(ShardStatusState.Running,
            $"A daemon is running and caught up, and the store reported '{shard.State}'. Unknown means there is no daemon to ask, which is not the case here.");

        shard.ProcessedSequence.ShouldBeGreaterThan(0,
            "The shard was reported as Running with no progression, so the state came from a registry rather than from the daemon.");

        shard.EventStoreSequence.ShouldBeGreaterThanOrEqualTo(shard.ProcessedSequence);
        shard.Error.ShouldBeNull();
    }
}
