using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record WingOpened(string Name);

/// <summary>
/// A deliberately plain self-aggregating snapshot type for the coordinator suite. The daemon has to
/// be projecting <em>something</em> for reachability and pause/resume to be observable facts rather
/// than claims about registration alone.
/// </summary>
public partial class WingRoster
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public void Apply(WingOpened e) => Name = e.Name;
}

/// <summary>
/// Proves a store registers a REACHABLE <see cref="IProjectionCoordinator"/> over a host built the
/// documented way — the product's own DI registration plus its documented async daemon
/// registration — and that both documented routes to it land on one instance (jasperfx#732).
/// </summary>
/// <remarks>
/// <para>
/// Every other daemon suite drives a daemon the fixture constructed by hand, which can never
/// observe whether the documented registration produces a coordinator application code can reach.
/// fisher#138 shipped exactly that gap: the store registered only an <c>IHostedService</c> over an
/// internal class implementing nothing else, so <c>GetRequiredService&lt;IProjectionCoordinator&gt;()</c>
/// threw and the <c>GetServices&lt;IHostedService&gt;().OfType&lt;IProjectionCoordinator&gt;()</c>
/// fallback found nothing — and the store passed all 37 suites the whole time. This is the third
/// instance of one pattern (jasperfx#700's unfilled usage slots, jasperfx#718's identity-carrying
/// DCB aggregates): the store is silently the odd one out, nothing dialect-specific prompts a look,
/// and the symptom surfaces in a downstream consumer.
/// </para>
/// <para>
/// Which is why this suite deliberately carries NO capability gate. The fixture seam
/// (<c>StartCoordinatorHostAsync</c>) has a throwing default so consumers keep compiling across the
/// bump, but a store that enrolls the suite without implementing it fails every fact rather than
/// skipping — a skippable registration check recreates the silent gap the suite exists to close.
/// The one gated fact is the ancillary marker-typed coordinator, gated on
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsAncillaryCoordinators"/>
/// because not every store has ancillary registration at all.
/// </para>
/// <para>
/// The same-instance fact is a correctness matter and not just tidiness: two registrations of one
/// coordinator are two daemons over one database, which for a single-writer store is two writers
/// contending for the same lock. And the pause/resume fact is the one that would have caught
/// fisher#138's real bug — its <c>StartAsync</c> only started daemons for databases not already
/// running, so a naive <c>ResumeAsync</c> restarted nothing. The first append-and-wait round
/// before the pause is load-bearing for the same reason: it proves the daemon was genuinely
/// running, so a post-resume timeout indicts resume rather than startup.
/// </para>
/// </remarks>
public abstract class ProjectionCoordinatorCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_coordinator";

        config.AddEventType<WingOpened>();

        config.Snapshot<WingRoster>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private async Task<Guid> OpenWingAsync(IComplianceCoordinatorHost<TOperations> host, string name)
    {
        var id = Guid.NewGuid();

        await using (var session = host.OpenSession())
        {
            EventsFor(session).StartStream<WingRoster>(id, new WingOpened(name));
            await SaveChangesAsync(session);
        }

        return id;
    }

    [Fact]
    public async Task the_coordinator_resolves_from_the_container()
    {
        await using var host = await StartCoordinatorHostAsync();

        host.Services.GetRequiredService<IProjectionCoordinator>().ShouldNotBeNull();
    }

    /// <summary>
    /// The documented fallback for a host that resolves hosted services rather than the interface —
    /// walking <c>GetServices&lt;IHostedService&gt;()</c> and casting — must find the SAME instance
    /// the container hands out, and exactly one of it. Two instances would be two daemons over one
    /// database; none is fisher#138. The strict single-item walk is why the default host registers
    /// only the primary store — an ancillary coordinator would be a second, legitimate item.
    /// </summary>
    [Fact]
    public async Task the_hosted_service_and_the_resolved_coordinator_are_one_instance()
    {
        await using var host = await StartCoordinatorHostAsync();

        var resolved = host.Services.GetRequiredService<IProjectionCoordinator>();

        var walked = host.Services.GetServices<IHostedService>()
            .OfType<IProjectionCoordinator>()
            .ShouldHaveSingleItem();

        walked.ShouldBeSameAs(resolved);
    }

    [Fact]
    public async Task the_main_database_daemon_is_reachable_and_projecting()
    {
        await using var host = await StartCoordinatorHostAsync();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var id = await OpenWingAsync(host, "East Wing");

        var daemon = coordinator.DaemonForMainDatabase();
        daemon.ShouldNotBeNull();
        await daemon.WaitForNonStaleData(_timeout);

        await using var query = host.OpenSession();
        var roster = await LoadDocumentAsync<WingRoster>(query, id);
        roster.ShouldNotBeNull();
        roster.Name.ShouldBe("East Wing");
    }

    /// <summary>
    /// Reference identity on purpose: the coordinator's whole job is to HOLD its daemons, which is
    /// what pause and resume operate on. A coordinator constructing a fresh daemon per call would
    /// pass a value-equality check while running two daemons over one database.
    /// </summary>
    [Fact]
    public async Task all_daemons_includes_the_main_database_daemon()
    {
        await using var host = await StartCoordinatorHostAsync();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var daemon = coordinator.DaemonForMainDatabase();

        var all = await coordinator.AllDaemonsAsync();
        all.Any(x => ReferenceEquals(x, daemon)).ShouldBeTrue();
    }

    /// <summary>
    /// The fact that would have caught fisher#138's real bug: pause must stop agents without
    /// disposing the daemons, and resume must restart agents on the daemons already held — a
    /// <c>StartAsync</c> that only starts daemons for databases not already running restarts
    /// nothing after a pause, and the second wait here times out naming shards that recorded no
    /// progress.
    /// </summary>
    [Fact]
    public async Task pause_then_resume_leaves_the_daemon_projecting()
    {
        await using var host = await StartCoordinatorHostAsync();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var before = await OpenWingAsync(host, "Before Pause");
        await coordinator.DaemonForMainDatabase().WaitForNonStaleData(_timeout);

        await coordinator.PauseAsync();
        await coordinator.ResumeAsync();

        var after = await OpenWingAsync(host, "After Resume");
        await coordinator.DaemonForMainDatabase().WaitForNonStaleData(_timeout);

        await using var query = host.OpenSession();

        var resumed = await LoadDocumentAsync<WingRoster>(query, after);
        resumed.ShouldNotBeNull();
        resumed.Name.ShouldBe("After Resume");

        var original = await LoadDocumentAsync<WingRoster>(query, before);
        original.ShouldNotBeNull();
        original.Name.ShouldBe("Before Pause");
    }

    /// <summary>
    /// The one gated fact, because not every store has ancillary store registration. When it does,
    /// the ancillary coordinator must be its own instance — one non-generic coordinator can only
    /// mean one store — and must itself be registered as a hosted service exactly once, for the
    /// same two-daemons reason as the main one.
    /// </summary>
    [Fact]
    public async Task an_ancillary_store_registers_a_marker_typed_coordinator()
    {
        Assert.SkipUnless(theFixture.SupportsAncillaryCoordinators,
            "This event store does not support ancillary store registration with marker-typed coordinators");

        await using var host = await StartCoordinatorHostAsync(includeAncillaryStore: true);

        var main = host.Services.GetRequiredService<IProjectionCoordinator>();
        var ancillary = theFixture.AncillaryCoordinatorFrom(host.Services);

        ancillary.ShouldNotBeNull();
        ancillary.ShouldNotBeSameAs(main);

        host.Services.GetServices<IHostedService>()
            .OfType<IProjectionCoordinator>()
            .Count(x => ReferenceEquals(x, ancillary))
            .ShouldBe(1);
    }
}
