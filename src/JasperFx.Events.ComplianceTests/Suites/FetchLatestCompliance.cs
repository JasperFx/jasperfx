using System;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region FetchLatest events and aggregate

public record TabOpened(string Customer);

public record DrinkOrdered(decimal Price);

public record TabClosed;

/// <summary>
/// Registered as an inline snapshot by the default configuration, and as nothing at all by the
/// live configuration, so the same aggregate can be read back both ways.
/// </summary>
public partial class ComplianceTab
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public bool Closed { get; set; }

    public static ComplianceTab Create(TabOpened e) => new() { Customer = e.Customer };

    public void Apply(DrinkOrdered e) => Total += e.Price;

    public void Apply(TabClosed _) => Closed = true;
}

#endregion

/// <summary>
/// <c>FetchLatest</c> and <c>ProjectLatest</c> — the "give me the current aggregate, and do not
/// make me care how it is stored" entry points.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour worth pinning is that the answer does not depend on the projection lifecycle. A
/// caller that switches an aggregate from Live to Inline should see identical state; only the
/// persistence timing changes. This suite asserts that directly by rebuilding the store with a
/// second configuration mid-test — the fixture keys rebuilds on the configuration delegate's
/// identity, so passing a different static delegate is the supported way to ask for a different
/// store shape.
/// </para>
/// <para>
/// The other half is that <c>FetchLatest</c> must fold events appended in the current, still
/// uncommitted session on top of whatever is persisted. That is the property that makes it safe
/// inside a command handler, and it is easy to lose when a store optimises the snapshot read.
/// </para>
/// </remarks>
public abstract class FetchLatestCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _inlineConfiguration = config =>
    {
        config.SchemaName = "compliance_fetch_latest";
        config.Snapshot<ComplianceTab>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _liveConfiguration = config =>
    {
        config.SchemaName = "compliance_fetch_latest_live";
        config.AddEventType<TabOpened>();
        config.AddEventType<DrinkOrdered>();
        config.AddEventType<TabClosed>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _inlineConfiguration;

    private async Task<Guid> aTabAsync(params object[] extra)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new TabOpened("Ada") };
        if (extra.Length > 0)
        {
            events = [.. events, .. extra];
        }

        EventsFor(session).StartStream<ComplianceTab>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task fetch_latest_returns_the_current_aggregate()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4), new DrinkOrdered(6));

        await using var session = OpenSession();
        var tab = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Customer.ShouldBe("Ada");
        tab.Total.ShouldBe(10);
    }

    [Fact]
    public async Task fetch_latest_for_an_unknown_stream_is_null()
    {
        await using var session = OpenSession();
        var tab = await EventsFor(session).FetchLatest<ComplianceTab>(Guid.NewGuid(), Cancellation);

        tab.ShouldBeNull();
    }

    /// <summary>
    /// The documented command-handler shape: <c>FetchForWriting</c>, append to the handle, save,
    /// then <c>FetchLatest</c> on the <em>same</em> session. Both products special-case this
    /// sequence, and the risk it carries is a stale read — the aggregate loaded for writing is
    /// still in the session, and returning that pre-append instance would be silently wrong.
    /// </summary>
    /// <remarks>
    /// What this deliberately does NOT assert is visibility of <em>unsaved</em> events. Neither
    /// product promises that, and neither delivers it: Marten's own test for this path saves before
    /// reading back. Two earlier drafts of this test asserted the stronger claim — first through a
    /// bare <c>Append</c>, then through the write handle — and both were wrong about the contract
    /// rather than finding a bug. Recorded here so the next reader does not re-litigate it.
    /// </remarks>
    [Fact]
    public async Task fetch_latest_after_fetch_for_writing_and_save_is_not_stale()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4));

        await using var session = OpenSession();

        var stream = await EventsFor(session).FetchForWriting<ComplianceTab>(streamId, Cancellation);
        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Total.ShouldBe(4);

        stream.AppendOne(new DrinkOrdered(6));
        await SaveChangesAsync(session);

        var tab = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Total.ShouldBe(10);
    }

    [Fact]
    public async Task fetch_latest_agrees_with_a_live_fold_of_the_same_stream()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4), new DrinkOrdered(6));

        await using var session = OpenSession();
        var fetched = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);
        var folded = await EventsFor(session).AggregateStreamAsync<ComplianceTab>(streamId, token: Cancellation);

        fetched.ShouldNotBeNull();
        folded.ShouldNotBeNull();
        folded.Total.ShouldBe(fetched.Total);
        folded.Customer.ShouldBe(fetched.Customer);
    }

    [Fact]
    public async Task fetch_latest_reflects_a_later_append()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4));

        await using (var writer = OpenSession())
        {
            EventsFor(writer).Append(streamId, new DrinkOrdered(6), new TabClosed());
            await SaveChangesAsync(writer);
        }

        await using var session = OpenSession();
        var tab = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Total.ShouldBe(10);
        tab.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task project_latest_agrees_with_fetch_latest()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4), new DrinkOrdered(6));

        await using var session = OpenSession();
        var fetched = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);
        var projected = await EventsFor(session).ProjectLatest<ComplianceTab>(streamId, Cancellation);

        fetched.ShouldNotBeNull();
        projected.ShouldNotBeNull();
        projected.Total.ShouldBe(fetched.Total);
        projected.Closed.ShouldBe(fetched.Closed);
    }

    /// <summary>
    /// The one thing that separates <c>ProjectLatest</c> from <c>FetchLatest</c>: events appended to
    /// the session but not yet committed are folded in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note the deliberate contrast with
    /// <see cref="fetch_latest_after_fetch_for_writing_and_save_is_not_stale"/>, whose remarks record
    /// that <c>FetchLatest</c> does <em>not</em> promise uncommitted visibility. <c>ProjectLatest</c>
    /// does, in both products, and that promise is what makes it usable inside a decider that wants
    /// to see the effect of what it has already decided. Without a fact here, the two entry points
    /// are indistinguishable from a compliance suite's point of view —
    /// <see cref="project_latest_agrees_with_fetch_latest"/> is satisfied by a store that simply
    /// aliases one to the other.
    /// </para>
    /// <para>
    /// Nothing is saved: the assertion runs against a session holding pending events, and the store
    /// must not have persisted anything to answer it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task project_latest_includes_events_pending_in_the_session()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceTab>(streamId,
            new TabOpened("Ada"), new DrinkOrdered(4), new DrinkOrdered(6));

        var tab = await EventsFor(session).ProjectLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Customer.ShouldBe("Ada");
        tab.Total.ShouldBe(10);
    }

    [Fact]
    public async Task project_latest_merges_committed_and_pending_events()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4));

        await using var session = OpenSession();
        EventsFor(session).Append(streamId, new DrinkOrdered(6), new TabClosed());

        var tab = await EventsFor(session).ProjectLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Total.ShouldBe(10);
        tab.Closed.ShouldBeTrue();

        // Still pending: the merge is a read-side fold, not an early commit.
        await using var other = OpenSession();
        var persisted = await EventsFor(other).FetchStreamStateAsync(streamId, Cancellation);
        persisted.ShouldNotBeNull();
        persisted.Version.ShouldBe(2);
    }

    /// <summary>
    /// <c>no_pending_events_behaves_like_fetch_latest</c> — the same fact both products carry
    /// verbatim. The merge must not cost correctness when there is nothing to merge.
    /// </summary>
    [Fact]
    public async Task project_latest_with_no_pending_events_behaves_like_fetch_latest()
    {
        var streamId = await aTabAsync(new DrinkOrdered(4), new DrinkOrdered(6));

        await using var session = OpenSession();

        var fromProjectLatest = await EventsFor(session).ProjectLatest<ComplianceTab>(streamId, Cancellation);
        var fromFetchLatest = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);

        fromProjectLatest.ShouldNotBeNull();
        fromFetchLatest.ShouldNotBeNull();
        fromProjectLatest.Customer.ShouldBe(fromFetchLatest.Customer);
        fromProjectLatest.Total.ShouldBe(fromFetchLatest.Total);
        fromProjectLatest.Closed.ShouldBe(fromFetchLatest.Closed);
    }

    [Fact]
    public async Task project_latest_for_an_unknown_stream_with_nothing_pending_is_null()
    {
        await using var session = OpenSession();
        var tab = await EventsFor(session).ProjectLatest<ComplianceTab>(Guid.NewGuid(), Cancellation);

        tab.ShouldBeNull();
    }

    [Fact]
    public async Task project_latest_gives_the_same_answer_with_no_snapshot_registered()
    {
        // Live: the aggregate is registered nowhere, so pending events have to be folded on top of a
        // live fold rather than on top of a persisted document. Both products get one of the two
        // paths right more easily than the other.
        await theFixture.ConfigureAsync(_liveConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using (var writer = OpenSession())
        {
            EventsFor(writer).StartStream<ComplianceTab>(streamId, new TabOpened("Ada"), new DrinkOrdered(4));
            await SaveChangesAsync(writer);
        }

        await using var session = OpenSession();
        EventsFor(session).Append(streamId, new DrinkOrdered(6), new TabClosed());

        var tab = await EventsFor(session).ProjectLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Customer.ShouldBe("Ada");
        tab.Total.ShouldBe(10);
        tab.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task fetch_latest_gives_the_same_answer_with_no_snapshot_registered()
    {
        // Rebuild the store with the aggregate registered nowhere, so FetchLatest has to fold the
        // stream rather than read a persisted document. Same events, same expected answer.
        await theFixture.ConfigureAsync(_liveConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceTab>(streamId,
            new TabOpened("Ada"), new DrinkOrdered(4), new DrinkOrdered(6));
        await SaveChangesAsync(session);

        var tab = await EventsFor(session).FetchLatest<ComplianceTab>(streamId, Cancellation);

        tab.ShouldNotBeNull();
        tab.Customer.ShouldBe("Ada");
        tab.Total.ShouldBe(10);
    }
}
