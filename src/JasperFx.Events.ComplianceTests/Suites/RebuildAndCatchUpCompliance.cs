using System;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Rebuild and catch-up events and aggregates

public record TurbineInstalled(string Site);

public record TurbineSpun(int Revolutions);

public record TurbineHalted;

public partial class ComplianceTurbine
{
    public Guid Id { get; set; }
    public string Site { get; set; } = string.Empty;
    public int TotalRevolutions { get; set; }
    public int SpinCount { get; set; }
    public bool Halted { get; set; }

    public static ComplianceTurbine Create(TurbineInstalled e) => new() { Site = e.Site };

    public void Apply(TurbineSpun e)
    {
        TotalRevolutions += e.Revolutions;
        SpinCount++;
    }

    public void Apply(TurbineHalted _) => Halted = true;
}

/// <summary>
/// A second async projection over the same events, so a rebuild of one can be shown not to disturb
/// the other. Counts only installations, so its state is trivially distinguishable.
/// </summary>
public partial class ComplianceTurbineAudit
{
    public Guid Id { get; set; }
    public string Site { get; set; } = string.Empty;
    public int Revisions { get; set; }

    public static ComplianceTurbineAudit Create(TurbineInstalled e) => new() { Site = e.Site, Revisions = 1 };

    public void Apply(TurbineSpun _) => Revisions++;
}

#endregion

/// <summary>
/// Projection rebuild and catch-up — replaying an async projection from the event stream, and the
/// guarantees a caller is entitled to about what the replay leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AsyncDaemonCompliance"/> already smoke-tests that a daemon catches up and that a
/// rebuild reproduces state. This suite is about the parts that are easy to get subtly wrong and
/// that have historically diverged: whether a rebuild <em>tears down</em> before it replays, whether
/// it is idempotent, whether the several ways of naming a projection agree, and whether rebuilding
/// one projection disturbs another.
/// </para>
/// <para>
/// <strong>The teardown assertion is the one that matters most.</strong> A rebuild that replays onto
/// whatever rows survived looks correct for every stream whose events still exist, and is wrong only
/// for the rows the replay can no longer produce — so it passes any test that checks a live
/// aggregate and fails only on stale state. That exact divergence was a real product gap found by
/// the flat-table suite (polecat#415), which is why this suite pins it for document projections too,
/// by planting a document the replay cannot possibly produce and requiring the rebuild to remove it.
/// </para>
/// <para>
/// No seam addition: the whole rebuild surface — <c>RebuildProjectionAsync</c> in its several
/// overloads, <c>CatchUpAsync</c>, <c>PrepareForRebuildsAsync</c> — is declared on the shared
/// <see cref="Daemon.IProjectionDaemon"/>, which the fixture already hands back from
/// <c>StartDaemonAsync</c>. That is why this landed in the "portable" group after all, despite
/// being filed as needing one (marten#5150).
/// </para>
/// </remarks>
public abstract class RebuildAndCatchUpCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_rebuild";
        config.Snapshot<ComplianceTurbine>(SnapshotLifecycle.Async);
        config.Snapshot<ComplianceTurbineAudit>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    private async Task<Guid> aTurbineAsync(string site = "North Ridge")
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceTurbine>(streamId,
            new TurbineInstalled(site),
            new TurbineSpun(10),
            new TurbineSpun(15));
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<ComplianceTurbine?> turbineAsync(Guid id)
    {
        await using var query = OpenSession();
        return await LoadDocumentAsync<ComplianceTurbine>(query, id);
    }

    [Fact]
    public async Task a_rebuild_reproduces_the_projected_state()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.Site.ShouldBe("North Ridge");
        turbine.TotalRevolutions.ShouldBe(25);
        turbine.SpinCount.ShouldBe(2);
    }

    /// <summary>
    /// The teardown guarantee: a rebuild starts from an empty slate, not from whatever survived.
    /// </summary>
    [Fact]
    public async Task a_rebuild_removes_state_the_replay_cannot_produce()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        // A document with no backing events at all. Nothing in the replay can produce it, so a
        // rebuild that tears down first must remove it, and a rebuild that merely replays on top
        // will leave it sitting there.
        var orphanId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            StoreDocument(session, new ComplianceTurbine
            {
                Id = orphanId, Site = "Nowhere", TotalRevolutions = 999, SpinCount = 99
            });
            await SaveChangesAsync(session);
        }

        (await turbineAsync(orphanId)).ShouldNotBeNull();

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        (await turbineAsync(orphanId)).ShouldBeNull();

        // ... and the real stream is still there afterwards.
        (await turbineAsync(streamId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task rebuilding_twice_is_idempotent()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);
        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();

        // Not 50 / 4 -- the second pass must replace rather than accumulate.
        turbine.TotalRevolutions.ShouldBe(25);
        turbine.SpinCount.ShouldBe(2);
    }

    [Fact]
    public async Task a_rebuild_picks_up_events_appended_after_the_first_catch_up()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new TurbineSpun(5), new TurbineHalted());
            await SaveChangesAsync(session);
        }

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.TotalRevolutions.ShouldBe(30);
        turbine.SpinCount.ShouldBe(3);
        turbine.Halted.ShouldBeTrue();
    }

    [Fact]
    public async Task rebuilding_one_projection_leaves_another_alone()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        await using var query = OpenSession();
        var audit = await LoadDocumentAsync<ComplianceTurbineAudit>(query, streamId);

        audit.ShouldNotBeNull();
        audit.Site.ShouldBe("North Ridge");
        audit.Revisions.ShouldBe(3);
    }

    /// <summary>
    /// Naming a projection by its view type and by its registered name have to select the same
    /// projection, or a caller who rebuilds by name silently rebuilds nothing.
    /// </summary>
    [Fact]
    public async Task rebuilding_by_projection_name_agrees_with_rebuilding_by_view_type()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        // Plant the orphan again -- if the named rebuild silently matched no projection, the orphan
        // would survive and this would fail, where a bare state assertion would not.
        var orphanId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            StoreDocument(session, new ComplianceTurbine { Id = orphanId, Site = "Nowhere" });
            await SaveChangesAsync(session);
        }

        await daemon.RebuildProjectionAsync(nameof(ComplianceTurbine), Cancellation);

        (await turbineAsync(orphanId)).ShouldBeNull();

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.TotalRevolutions.ShouldBe(25);
    }

    [Fact]
    public async Task rebuilding_a_projection_with_no_events_is_not_an_error()
    {
        SkipUnlessDaemonIsSupported();

        var daemon = await StartDaemonAsync();

        // No streams at all in this store yet.
        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        await using var query = OpenSession();
        var turbine = await LoadDocumentAsync<ComplianceTurbine>(query, Guid.NewGuid());
        turbine.ShouldBeNull();
    }

    [Fact]
    public async Task catch_up_advances_the_projection_without_a_full_daemon_run()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await daemon.CatchUpAsync(_timeout, Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.TotalRevolutions.ShouldBe(25);
    }

    [Fact]
    public async Task catch_up_is_safe_to_call_when_there_is_nothing_to_do()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await daemon.CatchUpAsync(_timeout, Cancellation);
        await daemon.CatchUpAsync(_timeout, Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.TotalRevolutions.ShouldBe(25);
        turbine.SpinCount.ShouldBe(2);
    }

    [Fact]
    public async Task prepare_for_rebuilds_leaves_the_daemon_able_to_rebuild()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await aTurbineAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.PrepareForRebuildsAsync();
        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        var turbine = await turbineAsync(streamId);
        turbine.ShouldNotBeNull();
        turbine.TotalRevolutions.ShouldBe(25);
    }

    [Fact]
    public async Task a_rebuild_reproduces_every_stream_not_just_the_last()
    {
        SkipUnlessDaemonIsSupported();

        var first = await aTurbineAsync("North Ridge");
        var second = await aTurbineAsync("South Bank");
        var third = await aTurbineAsync("East Flats");

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<ComplianceTurbine>(Cancellation);

        foreach (var (id, site) in new[] { (first, "North Ridge"), (second, "South Bank"), (third, "East Flats") })
        {
            var turbine = await turbineAsync(id);
            turbine.ShouldNotBeNull();
            turbine.Site.ShouldBe(site);
            turbine.TotalRevolutions.ShouldBe(25);
        }
    }
}
