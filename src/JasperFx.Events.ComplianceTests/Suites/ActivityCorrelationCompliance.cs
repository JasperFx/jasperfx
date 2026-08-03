using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record ActivityQuestStarted(string Name);

/// <summary>
/// Opening a session seeds <c>CorrelationId</c> from <c>Activity.Current.RootId</c> and
/// <c>CausationId</c> from <c>Activity.Current.ParentId</c>, so distributed tracing context reaches
/// appended events with no application code. An explicit caller assignment still wins.
/// </summary>
/// <remarks>
/// The seeding happens at session construction, which is why every test here opens its session
/// inside the activity scope rather than in a fixture. Both stores read the ambient
/// <see cref="Activity"/> the same way; the only thing that differs is which options object carries
/// the "persist this metadata" switch, and that is what
/// <see cref="ComplianceStoreConfig.EnableCorrelationTracking"/> absorbs.
/// </remarks>
public abstract class ActivityCorrelationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_activity";

        // Only the second test needs the metadata actually persisted onto events, but turning it on
        // for the whole suite costs nothing: the session-level seeding under test is independent of
        // whether the store writes the columns.
        config.EnableCorrelationTracking = true;
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// A started activity nested inside another, so <c>ParentId</c> is non-null and distinguishable
    /// from <c>RootId</c>.
    /// </summary>
    private static (Activity Parent, Activity Child) startActivityScope()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        var parent = new Activity("compliance-parent").Start();
        var child = new Activity("compliance-child").Start();

        return (parent, child);
    }

    [Fact]
    public async Task session_seeds_correlation_and_causation_from_activity()
    {
        var (parent, child) = startActivityScope();

        try
        {
            await using var session = OpenSession();

            CorrelationIdFor(session).ShouldBe(child.RootId);
            CausationIdFor(session).ShouldBe(child.ParentId);
        }
        finally
        {
            child.Stop();
            parent.Stop();
        }
    }

    [Fact]
    public async Task events_appended_in_activity_scope_carry_root_and_parent_ids()
    {
        var (parent, child) = startActivityScope();
        var streamId = Guid.NewGuid();

        try
        {
            await using var session = OpenSession();
            EventsFor(session).StartStream(streamId, new ActivityQuestStarted("Traced Quest"));
            await SaveChangesAsync(session);
        }
        finally
        {
            child.Stop();
            parent.Stop();
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(1);
        events[0].CorrelationId.ShouldBe(child.RootId);
        events[0].CausationId.ShouldBe(child.ParentId);
    }

    [Fact]
    public async Task explicit_caller_value_wins_over_activity()
    {
        var (parent, child) = startActivityScope();

        try
        {
            await using var session = OpenSession();
            SetCorrelationId(session, "explicit-corr");

            CorrelationIdFor(session).ShouldBe("explicit-corr");
            CorrelationIdFor(session).ShouldNotBe(child.RootId);
        }
        finally
        {
            child.Stop();
            parent.Stop();
        }
    }

    [Fact]
    public async Task no_activity_leaves_correlation_null()
    {
        var ambient = Activity.Current;
        Activity.Current = null;

        try
        {
            await using var session = OpenSession();

            CorrelationIdFor(session).ShouldBeNull();
            CausationIdFor(session).ShouldBeNull();
        }
        finally
        {
            Activity.Current = ambient;
        }
    }
}
