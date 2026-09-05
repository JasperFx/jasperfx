using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Aggregate-to-many sample types

public record ComplianceMoneyDeposited(Guid AccountId, int Amount);

public record ComplianceAccountFrozen(Guid AccountId);

public class ComplianceBalance
{
    public Guid Id { get; set; }
    public int Amount { get; set; }
}

/// <summary>
/// The straightforward multi-stream projection under compliance: identity-routed, with a
/// <c>ShouldDelete</c> so the suite can prove deleted identities fall out of the result. The base
/// type arrives through the consumer-supplied <c>ComplianceBalanceProjectionBase</c> global alias,
/// exactly like <see cref="ComplianceDepartmentProjection"/>'s.
/// </summary>
public partial class ComplianceBalanceProjection: ComplianceBalanceProjectionBase
{
    public ComplianceBalanceProjection()
    {
        Identity<ComplianceMoneyDeposited>(e => e.AccountId);
        Identity<ComplianceAccountFrozen>(e => e.AccountId);
    }

    public void Apply(ComplianceMoneyDeposited e, ComplianceBalance b) => b.Amount += e.Amount;

    public bool ShouldDelete(ComplianceAccountFrozen e) => true;
}

public record ComplianceLoyaltyEarned(Guid CardId, int Points);

/// <summary>
/// Present-day reference data: which member owns a card. Stored as a plain document, never an
/// event, so the grouper below has to read it from the live session.
/// </summary>
public class ComplianceCardOwner
{
    public Guid Id { get; set; } // card id
    public Guid MemberId { get; set; }
}

public class ComplianceMemberLoyalty
{
    public Guid Id { get; set; }
    public int Points { get; set; }
}

/// <summary>
/// The enrichment-shaped projection: events are keyed by card, the aggregate by member, and only a
/// database lookup can translate one to the other. Its grouper implements the shared
/// <see cref="IJasperFxAggregateGrouper{TId,TQuerySession}"/> against the consumer's
/// <c>ComplianceQuerySession</c> alias, and loads through
/// <c>LoadAsync</c> — the same session member the enrichment suite already binds.
/// </summary>
public partial class ComplianceMemberLoyaltyProjection: ComplianceMemberLoyaltyProjectionBase
{
    public ComplianceMemberLoyaltyProjection()
    {
        CustomGrouping(new Grouper());
    }

    public void Apply(ComplianceLoyaltyEarned e, ComplianceMemberLoyalty agg) => agg.Points += e.Points;

    public class Grouper: IJasperFxAggregateGrouper<Guid, ComplianceQuerySession>
    {
        public async Task Group(ComplianceQuerySession session, IReadOnlyList<IEvent> events,
            IEventGrouping<Guid> grouping)
        {
            foreach (var e in events.OfType<IEvent<ComplianceLoyaltyEarned>>())
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

#endregion

/// <summary>
/// <c>AggregateToManyAsync&lt;T&gt;</c> — running an ad hoc event query through the multi-stream
/// projection registered for <typeparamref name="T"/> and getting one aggregate per resulting
/// identity, driven by the projection's real slicer/grouper and per-slice build against the live
/// session (marten#4998, polecat#364).
/// </summary>
/// <remarks>
/// <para>
/// Worth pinning cross-store because the operator's whole promise is "the same answer the
/// projection would give": the projection's own identity routing, its custom grouper reading
/// reference data from the session it was handed, and its <c>ShouldDelete</c> decisions. A store
/// that reimplemented any of those inline — rather than driving the registered projection — would
/// return plausible aggregates that quietly diverge from the persisted ones.
/// </para>
/// <para>
/// The projections are registered <see cref="ProjectionLifecycle.Async"/> and the daemon is never
/// started, which is load-bearing: every aggregate asserted here can only have come from the live
/// fold, never from a persisted document.
/// </para>
/// <para>
/// Opt-in: everything runs through the
/// <c>EventStoreComplianceFixture.AggregateEventsToManyAsync</c> seam (the raw-event LINQ query is
/// deliberately not part of the shared contract), gated on
/// <c>SupportsAggregateToLinqOperators</c>.
/// </para>
/// </remarks>
public abstract class AggregateToManyCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_aggregate_to_many";

        config.AddEventType<ComplianceMoneyDeposited>();
        config.AddEventType<ComplianceAccountFrozen>();
        config.AddEventType<ComplianceLoyaltyEarned>();

        config.AddProjection(new ComplianceBalanceProjection(), ProjectionLifecycle.Async);
        config.AddProjection(new ComplianceMemberLoyaltyProjection(), ProjectionLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAggregateToLinqOperators,
            "This event store has not implemented the AggregateTo LINQ operators under test");
    }

    [Fact]
    public async Task fans_a_cross_stream_query_out_to_one_aggregate_per_identity()
    {
        SkipUnlessSupported();

        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        await using var session = OpenSession();

        // acctA deposited across two streams; acctB in one — the fan-out keys on AccountId, not stream.
        EventsFor(session).Append(stream1,
            new ComplianceMoneyDeposited(acctA, 100),
            new ComplianceMoneyDeposited(acctB, 50));
        EventsFor(session).Append(stream2, new ComplianceMoneyDeposited(acctA, 25));
        await SaveChangesAsync(session);

        var aggregates = await theFixture.AggregateEventsToManyAsync<ComplianceBalance>(session,
            e => e.StreamId == stream1 || e.StreamId == stream2, Cancellation);

        aggregates.Count.ShouldBe(2);

        // Identity is stamped on each aggregate...
        aggregates.Single(x => x.Id == acctA).Amount.ShouldBe(125);
        aggregates.Single(x => x.Id == acctB).Amount.ShouldBe(50);
    }

    [Fact]
    public async Task excludes_aggregates_that_should_delete()
    {
        SkipUnlessSupported();

        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        var stream = Guid.NewGuid();

        await using var session = OpenSession();

        EventsFor(session).Append(stream,
            new ComplianceMoneyDeposited(acctA, 100),
            new ComplianceMoneyDeposited(acctB, 200),
            new ComplianceAccountFrozen(acctB));
        await SaveChangesAsync(session);

        var aggregates = await theFixture.AggregateEventsToManyAsync<ComplianceBalance>(session,
            e => e.StreamId == stream, Cancellation);

        // acctB is frozen (ShouldDelete) and so is absent; only acctA survives.
        aggregates.Count.ShouldBe(1);
        aggregates.Single().Id.ShouldBe(acctA);
        aggregates.Single().Amount.ShouldBe(100);
    }

    [Fact]
    public async Task enrichment_reads_reference_data_from_the_live_session()
    {
        SkipUnlessSupported();

        var cardA = Guid.NewGuid();
        var cardB = Guid.NewGuid();
        var cardC = Guid.NewGuid();
        var memberX = Guid.NewGuid();
        var memberY = Guid.NewGuid();

        await using var session = OpenSession();

        // Present-day reference data the grouper reads while the fold runs.
        StoreDocument(session, new ComplianceCardOwner { Id = cardA, MemberId = memberX });
        StoreDocument(session, new ComplianceCardOwner { Id = cardB, MemberId = memberX });
        StoreDocument(session, new ComplianceCardOwner { Id = cardC, MemberId = memberY });
        await SaveChangesAsync(session);

        var stream = Guid.NewGuid();
        EventsFor(session).Append(stream,
            new ComplianceLoyaltyEarned(cardA, 10),
            new ComplianceLoyaltyEarned(cardB, 5),
            new ComplianceLoyaltyEarned(cardC, 20));
        await SaveChangesAsync(session);

        var aggregates = await theFixture.AggregateEventsToManyAsync<ComplianceMemberLoyalty>(session,
            e => e.StreamId == stream, Cancellation);

        // Member-keyed, not card-keyed — only possible because the grouper read ComplianceCardOwner
        // from the session.
        aggregates.Count.ShouldBe(2);
        aggregates.Single(x => x.Id == memberX).Points.ShouldBe(15);
        aggregates.Single(x => x.Id == memberY).Points.ShouldBe(20);
    }

    [Fact]
    public async Task empty_query_returns_empty_list()
    {
        SkipUnlessSupported();

        await using var session = OpenSession();

        var nothing = Guid.NewGuid(); // matches no stream
        var aggregates = await theFixture.AggregateEventsToManyAsync<ComplianceBalance>(session,
            e => e.StreamId == nothing, Cancellation);

        aggregates.ShouldBeEmpty();
    }

    [Fact]
    public async Task throws_when_no_projection_produces_the_aggregate_type()
    {
        SkipUnlessSupported();

        await using var session = OpenSession();

        // Even over an empty result set — a missing projection is a programming error, not an
        // empty answer.
        await Should.ThrowAsync<ArgumentException>(() =>
            theFixture.AggregateEventsToManyAsync<ComplianceUnrelatedAggregate>(session, null, Cancellation));
    }
}

/// <summary>
/// No projection produces this type, which is the point of
/// <c>throws_when_no_projection_produces_the_aggregate_type</c>.
/// </summary>
public class ComplianceUnrelatedAggregate
{
    public Guid Id { get; set; }
}
