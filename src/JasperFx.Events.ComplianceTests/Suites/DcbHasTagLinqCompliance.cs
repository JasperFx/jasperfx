using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The <c>IEvent.HasTag&lt;TTag&gt;(value)</c> LINQ marker — a DCB tag predicate composing into the
/// same <c>Where()</c> as ordinary event predicates over a raw-event query (marten#4999,
/// polecat#364). AND-of-tag-predicates plus normal predicates is the pinned scope;
/// <c>EventTagQuery</c> with <c>QueryByTagsAsync</c> remains the OR/rich escape hatch, and has its
/// own suite in <see cref="DcbTagQueryAndConsistencyCompliance{TFixture,TOperations,TQuerySession}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the DCB tag surface adjacent to
/// <see cref="AssignTagWhereCompliance{TFixture,TOperations,TQuerySession}"/>, not general LINQ:
/// what is pinned is the tag predicate's semantics — matches only the tagged events, ANDs with
/// event-type/timestamp/tag predicates in one predicate tree, scopes to the session's tenant,
/// throws for an unregistered tag type — never a query provider's operator set.
/// </para>
/// <para>
/// Opt-in, through two fixture seam members with throwing defaults, gated on
/// <c>SupportsHasTagLinqPredicates</c>. <c>HasTagFilter</c> exists because the marker cannot be
/// spelled here at all: both products' LINQ parsers recognize it by the method's <em>declaring
/// type</em>, so the expression has to invoke the store's own extension.
/// <c>QueryRawEventsAsync</c> takes exactly one predicate for one <c>Where()</c>, and the
/// composition facts build their AND trees with <see cref="And"/> so the parser meets
/// <c>HasTag</c> as an <c>AndAlso</c> operand — the same tree the products' local tests compile —
/// rather than as a lone predicate ANDed at the SQL level by chained <c>Where()</c> calls.
/// </para>
/// <para>
/// Reuses the <see cref="StudentId"/> / <see cref="CourseId"/> tag types and
/// <see cref="StudentEnrolled"/> / <see cref="AssignmentSubmitted"/> events from the DCB
/// consistency suite, and <see cref="UnregisteredTagId"/> from the AssignTagWhere suite.
/// </para>
/// </remarks>
public abstract class DcbHasTagLinqCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_hastag_linq";

        config.AddEventType<StudentEnrolled>();
        config.AddEventType<AssignmentSubmitted>();

        config.RegisterTagType<StudentId>("student");
        config.RegisterTagType<CourseId>("course");
    };

    /// <summary>
    /// The same tag registration under conjoined tenancy, in its own schema — the event table's
    /// shape differs under tenancy, so the single-tenant tests keep their own store.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_hastag_linq_tenants";
        config.ConjoinedEventTenancy = true;

        config.AddEventType<StudentEnrolled>();
        config.AddEventType<AssignmentSubmitted>();

        config.RegisterTagType<StudentId>("student");
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsHasTagLinqPredicates,
            "This event store has not implemented the HasTag LINQ predicate under test");
    }

    private Task<IReadOnlyList<IEvent>> queryAsync(TOperations session, Expression<Func<IEvent, bool>> filter)
        => theFixture.QueryRawEventsAsync(session, filter, Cancellation);

    /// <summary>
    /// Combine two predicates into the single <c>e =&gt; left &amp;&amp; right</c> tree the C#
    /// compiler would emit for one <c>Where()</c> — which is the shape under test, since the
    /// products' parsers have to handle <c>HasTag</c> as an operand of <c>AndAlso</c>.
    /// </summary>
    private static Expression<Func<IEvent, bool>> And(Expression<Func<IEvent, bool>> left,
        Expression<Func<IEvent, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rewritten = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<IEvent, bool>>(Expression.AndAlso(left.Body, rewritten), parameter);
    }

    private sealed class ParameterReplacer: ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }

    [Fact]
    public async Task has_tag_matches_only_events_carrying_that_tag_value()
    {
        SkipUnlessSupported();

        var alice = new StudentId(Guid.NewGuid());
        var bob = new StudentId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new StudentEnrolled("Alice", "Math"), alice);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new StudentEnrolled("Bob", "Math"), bob);

        var events = await queryAsync(session, theFixture.HasTagFilter(alice));

        events.Count.ShouldBe(1);
        events.Single().Data.ShouldBeOfType<StudentEnrolled>().StudentName.ShouldBe("Alice");
    }

    [Fact]
    public async Task has_tag_composes_with_a_normal_event_predicate_in_one_where()
    {
        SkipUnlessSupported();

        var alice = new StudentId(Guid.NewGuid());
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();

        // Two events for Alice, both tagged with her StudentId, but of different event types.
        await AppendTaggedEventAsync(session, streamId, new StudentEnrolled("Alice", "Math"), alice);
        await AppendTaggedEventAsync(session, streamId, new AssignmentSubmitted("HW1", 95), alice);

        // "Alice's events, but only the enrollments" — the tag predicate AND a normal event
        // predicate, in one Where.
        var enrolledTypeName = EventTypeNameFor<StudentEnrolled>();
        var events = await queryAsync(session,
            And(theFixture.HasTagFilter(alice), e => e.EventTypeName == enrolledTypeName));

        events.Count.ShouldBe(1);
        events.Single().Data.ShouldBeOfType<StudentEnrolled>();
    }

    [Fact]
    public async Task has_tag_composes_with_a_timestamp_predicate()
    {
        SkipUnlessSupported();

        var alice = new StudentId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new StudentEnrolled("Alice", "Math"), alice);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

        var events = await queryAsync(session,
            And(theFixture.HasTagFilter(alice), e => e.Timestamp > cutoff));

        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task and_of_two_tag_predicates_requires_both_tags()
    {
        SkipUnlessSupported();

        var alice = new StudentId(Guid.NewGuid());
        var math = new CourseId(Guid.NewGuid());

        await using var session = OpenSession();

        // Event 1 carries BOTH tags; event 2 carries only the student tag.
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new StudentEnrolled("Alice", "Math"), alice, math);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new StudentEnrolled("Alice", "Science"), alice);

        var events = await queryAsync(session,
            And(theFixture.HasTagFilter(alice), theFixture.HasTagFilter(math)));

        // Only the event tagged with both survives the AND.
        events.Count.ShouldBe(1);
        events.Single().Data.ShouldBeOfType<StudentEnrolled>().CourseName.ShouldBe("Math");
    }

    [Fact]
    public async Task has_tag_is_isolated_by_tenant_under_conjoined_tenancy()
    {
        SkipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store cannot slice one database by tenant");

        await theFixture.ConfigureAsync(_conjoinedConfiguration);

        var alice = new StudentId(Guid.NewGuid());

        // The SAME tag value is appended in two tenants; HasTag must only match the session's
        // tenant. The failure mode is silent and asymmetric — a leaking store still answers
        // correctly for the tenant that owns the data — so the query runs from the tenant that
        // must NOT see the other's event.
        await using var redSession = await openForTenantAsync("red");
        await AppendTaggedEventAsync(redSession, Guid.NewGuid(), new StudentEnrolled("Alice", "Math"), alice);

        await using var blueSession = await openForTenantAsync("blue");
        await AppendTaggedEventAsync(blueSession, Guid.NewGuid(), new StudentEnrolled("Alice", "Science"), alice);

        var redEvents = await queryAsync(redSession, theFixture.HasTagFilter(alice));

        redEvents.Count.ShouldBe(1);
        redEvents.Single().Data.ShouldBeOfType<StudentEnrolled>().CourseName.ShouldBe("Math");
    }

    [Fact]
    public async Task has_tag_for_an_unregistered_tag_type_throws()
    {
        SkipUnlessSupported();

        var unknown = new UnregisteredTagId(Guid.NewGuid());

        await using var session = OpenSession();

        // The throw comes at query translation, not at predicate construction.
        await ShouldFailWithAsync<InvalidOperationException>(() =>
            queryAsync(session, theFixture.HasTagFilter(unknown)));
    }

    /// <summary>
    /// A session bound to one tenant, through the shared generic store surface — the same route
    /// <see cref="ConjoinedEventTenancyCompliance{TFixture,TOperations,TQuerySession}"/> uses.
    /// </summary>
    private async Task<TOperations> openForTenantAsync(string tenantId)
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

        var databases = await theFixture.EventStore.AllDatabases();
        var database = databases.First();

        return store.OpenSession(database, tenantId);
    }
}
