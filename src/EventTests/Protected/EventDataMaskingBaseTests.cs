using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Protected;
using Shouldly;

namespace EventTests.Protected;

/// <summary>
/// The shared masking pass (#1048). Masking rewrites protected data in place, so these are mostly about how it
/// fails: what a pass does when a rule matches nothing, fails part way, or is pointed at a tenant or at an
/// archived stream.
/// </summary>
public class EventDataMaskingBaseTests
{
    private const string Masked = "*****";

    private static readonly Guid theStream = Guid.NewGuid();
    private static readonly Guid otherStream = Guid.NewGuid();

    private readonly FakeEventRows theRows = new();
    private readonly EventRegistry theRegistry = new();
    private readonly TestMasking theMasking;

    public EventDataMaskingBaseTests()
    {
        theMasking = new TestMasking(theRows, e => theRegistry.TryMask(e));
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x with { Email = Masked });
    }

    private static string emailOf(EventRow row) => ((SubjectRegistered)row.Body).Email;

    [Fact]
    public async Task masks_the_events_of_an_included_stream_and_only_those()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 2, theStream, new SubjectClosed("resolved"));
        theRows.Add(3, 1, otherStream, new SubjectRegistered("tom@example.com"));

        await theMasking.IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
        theRows.Row(2).Body.ShouldBe(new SubjectClosed("resolved"));
        emailOf(theRows.Row(3)).ShouldBe("tom@example.com");
    }

    [Fact]
    public async Task masks_a_string_identified_stream()
    {
        theRows.Add(1, 1, Guid.Empty, new SubjectRegistered("hilda@example.com"), streamKey: "subject/hilda");

        await theMasking.IncludeStream("subject/hilda").ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public async Task a_stream_filter_narrows_the_selection()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 2, theStream, new SubjectRegistered("hilda@work.example.com"));

        await theMasking.IncludeStream(theStream, e => e.Version == 2).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe("hilda@example.com");
        emailOf(theRows.Row(2)).ShouldBe(Masked);
    }

    [Fact]
    public async Task include_events_selects_across_streams()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 1, otherStream, new SubjectRegistered("tom@example.com"));

        await theMasking.IncludeEvents(e => e.Version == 1).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
        emailOf(theRows.Row(2)).ShouldBe(Masked);
    }

    [Fact]
    public async Task headers_land_on_the_masked_events_only()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 2, theStream, new SubjectClosed("resolved"));

        await theMasking.IncludeStream(theStream).AddHeader("erasure", "case-17").ApplyAsync();

        theRows.Row(1).Headers.ShouldNotBeNull()["erasure"].ShouldBe("case-17");
        theRows.Row(2).Headers.ShouldBeNull();
    }

    [Fact]
    public async Task a_batch_naming_nothing_is_refused_before_a_session_is_opened()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => theMasking.ApplyAsync());

        ex.Message.ShouldContain("at least one stream or event filter");
        theMasking.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public void a_selection_that_could_only_ever_find_nothing_is_refused_as_it_is_built()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => theMasking.IncludeStream(Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => theMasking.IncludeStream(Guid.Empty, _ => true));
        Should.Throw<ArgumentException>(() => theMasking.IncludeStream(""));
        Should.Throw<ArgumentNullException>(() => theMasking.IncludeStream((string)null!));
        Should.Throw<ArgumentException>(() => theMasking.IncludeStream("", _ => true));
        Should.Throw<ArgumentNullException>(() => theMasking.IncludeStream(theStream, null!));
        Should.Throw<ArgumentNullException>(() => theMasking.IncludeStream("subject/hilda", null!));
        Should.Throw<ArgumentNullException>(() => theMasking.IncludeEvents(null!));
        Should.Throw<ArgumentNullException>(() => theMasking.AddHeader(null!, "value"));
    }

    // ---- a rule matching nothing ----

    [Fact]
    public async Task a_selection_no_rule_matches_writes_nothing_and_is_not_an_error()
    {
        theRows.Add(1, 1, theStream, new SubjectClosed("resolved"));

        await theMasking.IncludeStream(theStream).AddHeader("erasure", "case-17").ApplyAsync();

        var session = theMasking.Sessions.Single();
        session.Queued.ShouldBeEmpty();

        theRows.Row(1).Body.ShouldBe(new SubjectClosed("resolved"));
        theRows.Row(1).Headers.ShouldBeNull();
    }

    [Fact]
    public async Task a_stream_with_no_events_writes_nothing_and_is_not_an_error()
    {
        await theMasking.IncludeStream(Guid.NewGuid()).ApplyAsync();

        theMasking.Sessions.Single().Queued.ShouldBeEmpty();
    }

    // ---- an event reached twice ----

    [Fact]
    public async Task an_event_reached_by_two_sources_runs_each_rule_once_and_is_written_once()
    {
        var applied = new List<string>();
        var registry = new EventRegistry();
        registry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x =>
        {
            applied.Add(x.Email);
            return x with { Email = Masked };
        });

        var masking = new TestMasking(theRows, e => registry.TryMask(e));
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        await masking.IncludeStream(theStream).IncludeEvents(e => e.Sequence == 1).ApplyAsync();

        applied.ShouldBe(["hilda@example.com"]);
        masking.Sessions.Single().Queued.ShouldBe(["overwrite"]);
        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public async Task a_rule_that_is_not_idempotent_does_not_run_over_its_own_output()
    {
        // The double apply at its worst, a session handing the same instance to both sources. Without the
        // dedupe this stores the masking of the masking, and a pseudonym stops matching anything else.
        theRows.ReadsShareInstances = true;

        var registry = new EventRegistry();
        registry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x with { Email = x.Email + "*" });

        var masking = new TestMasking(theRows, e => registry.TryMask(e));
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda"));

        await masking.IncludeStream(theStream).IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe("hilda*");
    }

    [Fact]
    public async Task the_same_sequence_in_two_tenants_is_two_events()
    {
        // seq_id is per tenant under partitioned events, so a selection spanning tenants has to mask both. Fisher's
        // Sequence-only key drops the second one as a duplicate and leaves its protected information in place.
        var masked = new List<string>();
        var masking = new TestMasking(theRows, e =>
        {
            masked.Add(e.TenantId);
            return true;
        }) { AcrossTenants = true };

        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"), tenant: "alpha");
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"), tenant: "beta");

        await masking.IncludeEvents(e => e.Sequence == 1).ApplyAsync();

        masked.ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task an_event_with_no_sequence_fails_the_pass_rather_than_writing_nothing()
    {
        var masking = new TestMasking(theRows, e => theRegistry.TryMask(e))
        {
            Unsequenced = [new Event<SubjectRegistered>(new SubjectRegistered("hilda@example.com"))]
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            masking.IncludeEvents(e => true).ApplyAsync());

        ex.Message.ShouldContain("no sequence");
        ex.Message.ShouldNotContain("hilda");
        masking.Sessions.Single().Saves.ShouldBe(0);
    }

    // ---- a partial failure ----

    [Fact]
    public async Task a_rule_failing_part_way_leaves_nothing_committed()
    {
        var registry = new EventRegistry();
        registry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x.Email == "tom@example.com"
            ? throw new DivideByZeroException()
            : x with { Email = Masked });

        var masking = new TestMasking(theRows, e => registry.TryMask(e));

        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 2, theStream, new SubjectRegistered("harriet@example.com"));
        theRows.Add(3, 1, otherStream, new SubjectRegistered("tom@example.com"));

        await Should.ThrowAsync<DivideByZeroException>(() =>
            masking.IncludeStream(theStream).IncludeStream(otherStream).ApplyAsync());

        var session = masking.Sessions.Single();

        // Two rewrites were queued and the third event never got there. The session is thrown away with them
        session.Queued.Count.ShouldBe(2);
        session.Saves.ShouldBe(0);
        session.IsDisposed.ShouldBeTrue();

        emailOf(theRows.Row(1)).ShouldBe("hilda@example.com");
        emailOf(theRows.Row(2)).ShouldBe("harriet@example.com");
    }

    [Fact]
    public async Task a_rule_returning_what_the_event_cannot_hold_fails_the_pass_and_commits_nothing()
    {
        // #1043 made this a failure rather than a skip. It is the failure a pass is most likely to meet
        var registry = new EventRegistry();
        registry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x with { Email = Masked });
        registry.AddMaskingRuleForProtectedInformation<PersonalEvent>(_ => new PersonForgotten(Masked));

        var masking = new TestMasking(theRows, e => registry.TryMask(e));

        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));
        theRows.Add(2, 2, theStream, new PersonRenamed("Hilda Ravenswood"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            masking.IncludeStream(theStream).ApplyAsync());

        ex.Message.ShouldContain(nameof(PersonForgotten));
        ex.Message.ShouldContain(nameof(PersonRenamed));

        var session = masking.Sessions.Single();
        session.Saves.ShouldBe(0);
        session.IsDisposed.ShouldBeTrue();

        // Masked earlier in the same pass, and not committed either
        emailOf(theRows.Row(1)).ShouldBe("hilda@example.com");
        theRows.Row(2).Body.ShouldBe(new PersonRenamed("Hilda Ravenswood"));
    }

    [Fact]
    public async Task a_later_source_failing_to_read_leaves_nothing_committed()
    {
        var failure = new TimeoutException();
        var masking = new TestMasking(theRows, e => theRegistry.TryMask(e)) { QueryFailure = failure };

        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            masking.IncludeStream(theStream).IncludeEvents(e => true).ApplyAsync());

        ex.ShouldBeSameAs(failure);

        var session = masking.Sessions.Single();
        session.Saves.ShouldBe(0);
        session.IsDisposed.ShouldBeTrue();
        emailOf(theRows.Row(1)).ShouldBe("hilda@example.com");
    }

    [Fact]
    public async Task a_commit_failing_leaves_nothing_committed_and_the_session_disposed()
    {
        var masking = new TestMasking(theRows, e => theRegistry.TryMask(e))
        {
            Configure = session => session.FailAtCommit = "overwrite"
        };

        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            masking.IncludeStream(theStream).ApplyAsync());

        masking.Sessions.Single().IsDisposed.ShouldBeTrue();
        emailOf(theRows.Row(1)).ShouldBe("hilda@example.com");
    }

    [Fact]
    public async Task a_pass_that_succeeds_disposes_its_session_too()
    {
        // Marten's and Polecat's copies never disposed it, on any path
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        await theMasking.IncludeStream(theStream).ApplyAsync();

        var session = theMasking.Sessions.Single();
        session.Saves.ShouldBe(1);
        session.IsDisposed.ShouldBeTrue();
    }

    // ---- tenant scoping ----

    [Fact]
    public async Task masking_one_tenant_leaves_the_same_numbered_event_in_another_tenant_alone()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("alpha@example.com"), tenant: "alpha");
        theRows.Add(1, 1, theStream, new SubjectRegistered("beta@example.com"), tenant: "beta");

        await theMasking.ForTenant("alpha").IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1, "alpha")).ShouldBe(Masked);
        emailOf(theRows.Row(1, "beta")).ShouldBe("beta@example.com");
    }

    [Fact]
    public async Task the_whole_pass_runs_on_one_session_for_that_tenant()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("alpha@example.com"), tenant: "alpha");
        theRows.Add(2, 1, otherStream, new SubjectRegistered("alpha@example.com"), tenant: "alpha");

        await theMasking.IncludeStream(theStream).IncludeEvents(e => e.Sequence == 2).ForTenant("alpha")
            .ApplyAsync();

        theMasking.Sessions.Single().TenantId.ShouldBe("alpha");
        emailOf(theRows.Row(1, "alpha")).ShouldBe(Masked);
        emailOf(theRows.Row(2, "alpha")).ShouldBe(Masked);
    }

    [Fact]
    public async Task with_no_tenant_the_store_opens_its_default()
    {
        await theMasking.IncludeStream(theStream).ApplyAsync();

        theMasking.RequestedTenants.ShouldBe([null]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void a_missing_tenant_is_refused_rather_than_read_as_the_default(string? tenantId)
    {
        // Read as the default tenant, this finds nothing of the tenant that was meant and reports success
        Should.Throw<ArgumentException>(() => theMasking.ForTenant(tenantId!));
    }

    // ---- an archived stream ----

    [Fact]
    public async Task an_archived_stream_is_masked()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"), archived: true);

        await theMasking.IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public async Task an_archived_string_identified_stream_is_masked()
    {
        theRows.Add(1, 1, Guid.Empty, new SubjectRegistered("hilda@example.com"), archived: true,
            streamKey: "subject/hilda");

        await theMasking.IncludeStream("subject/hilda", _ => true).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public async Task archived_events_matching_a_filter_are_masked()
    {
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"), archived: true);
        theRows.Add(2, 1, otherStream, new SubjectRegistered("tom@example.com"));

        await theMasking.IncludeEvents(e => e.Version == 1).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
        emailOf(theRows.Row(2)).ShouldBe(Masked);
    }

    [Fact]
    public async Task the_plain_stream_read_would_have_found_nothing_in_an_archived_stream()
    {
        // What the seam is there to avoid. Marten's and Polecat's FetchStreamAsync leave archived events out, so
        // through it an archived stream selects nothing and the pass reports success.
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"), archived: true);

        await using var session = theRows.Session();

        (await session.Events.FetchStreamAsync(theStream, token: TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    // ---- a store part way through adopting #1043 ----

    [Fact]
    public async Task a_store_that_has_not_adopted_the_shared_rules_still_masks_through_its_own()
    {
        var graph = new GraphWithItsOwnRules();
        graph.AddMaskingRuleForProtectedInformation((SubjectRegistered x) => x with { Email = Masked });

        var masking = new TestMasking(theRows, e => graph.TryMask(e));
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        await masking.IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public async Task the_same_wiring_masks_through_the_shared_rules_once_it_has()
    {
        var graph = new GraphOnTheSharedRules();
        graph.AddMaskingRuleForProtectedInformation((SubjectRegistered x) => x with { Email = Masked });

        var masking = new TestMasking(theRows, e => graph.TryMask(e));
        theRows.Add(1, 1, theStream, new SubjectRegistered("hilda@example.com"));

        await masking.IncludeStream(theStream).ApplyAsync();

        emailOf(theRows.Row(1)).ShouldBe(Masked);
    }

    [Fact]
    public void a_registry_reference_to_a_store_with_its_own_rules_sees_none_of_them()
    {
        // Why the base class takes no EventRegistry. Through this reference the rule list is empty, so a pass
        // would report success having masked nothing.
        var graph = new GraphWithItsOwnRules();
        graph.AddMaskingRuleForProtectedInformation((SubjectRegistered x) => x with { Email = Masked });

        var @event = new Event<SubjectRegistered>(new SubjectRegistered("hilda@example.com"));

        ((EventRegistry)graph).TryMask(@event).ShouldBeFalse();
        graph.TryMask(@event).ShouldBeTrue();
    }

    // ---- a compacted stream ----

    [Fact]
    public async Task a_compacted_snapshot_takes_a_rule_of_its_own()
    {
        var snapshot = new Compacted<SubjectRegistered>(new SubjectRegistered("hilda@example.com"), theStream, "");
        theRows.Add(5, 5, theStream, snapshot);

        await theMasking.IncludeStream(theStream).ApplyAsync();

        // The rule against the folded event type matches nothing here, and the data is still in the snapshot
        theRows.Row(5).Body.ShouldBe(snapshot);

        theRegistry.AddMaskingRuleForProtectedInformation<Compacted<SubjectRegistered>>(x =>
            x with { Snapshot = x.Snapshot with { Email = Masked } });

        await new TestMasking(theRows, e => theRegistry.TryMask(e)).IncludeStream(theStream).ApplyAsync();

        ((Compacted<SubjectRegistered>)theRows.Row(5).Body).Snapshot.Email.ShouldBe(Masked);
    }

    public record SubjectRegistered(string Email);

    public record SubjectClosed(string Reason);

    public abstract record PersonalEvent(string Name);

    public record PersonRenamed(string Name) : PersonalEvent(Name);

    public record PersonForgotten(string Name) : PersonalEvent(Name);

    /// <summary>
    /// A store before #1043: a rule list of its own, behind members hiding the ones it inherits.
    /// </summary>
    private class GraphWithItsOwnRules : EventRegistry
    {
        private readonly EventRegistry _ownRules = new();

        public new void AddMaskingRuleForProtectedInformation<T>(Func<T, T> func)
            => _ownRules.AddMaskingRuleForProtectedInformation(func);

        public new bool TryMask(IEvent @event) => _ownRules.TryMask(@event);
    }

    /// <summary>
    /// The same store once it has deleted those members.
    /// </summary>
    private class GraphOnTheSharedRules : EventRegistry;

    private class TestMasking(FakeEventRows rows, Func<IEvent, bool> mask) : EventDataMaskingBase<FakeSession>
    {
        public List<string?> RequestedTenants { get; } = [];
        public List<FakeSession> Sessions { get; } = [];
        public Action<FakeSession>? Configure { get; init; }
        public Exception? QueryFailure { get; init; }
        public bool AcrossTenants { get; init; }
        public IReadOnlyList<IEvent>? Unsequenced { get; init; }

        protected override FakeSession OpenSession(string? tenantId)
        {
            RequestedTenants.Add(tenantId);

            var session = rows.Session(tenantId ?? FakeEventRows.DefaultTenant);
            Configure?.Invoke(session);
            Sessions.Add(session);

            return session;
        }

        protected override IEventStoreOperations EventsFor(FakeSession session) => session.Events;

        protected override Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(FakeSession session,
            Guid streamId, CancellationToken token)
            => Task.FromResult(session.Read(x => x.StreamId == streamId));

        protected override Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(FakeSession session,
            string streamKey, CancellationToken token)
            => Task.FromResult(session.Read(x => x.StreamKey == streamKey));

        protected override Task<IReadOnlyList<IEvent>> QueryEventsIncludingArchivedAsync(FakeSession session,
            Expression<Func<IEvent, bool>> filter, CancellationToken token)
        {
            if (QueryFailure is not null)
            {
                throw QueryFailure;
            }

            if (Unsequenced is not null)
            {
                return Task.FromResult(Unsequenced);
            }

            var matches = filter.Compile();

            IReadOnlyList<IEvent> events = (AcrossTenants ? rows.Rows.Select(x => x.Hydrate()) : session.Read(_ => true))
                .Where(matches)
                .ToList();

            return Task.FromResult(events);
        }

        protected override bool TryMask(IEvent @event) => mask(@event);

        protected override Task SaveChangesAsync(FakeSession session, CancellationToken token)
            => session.SaveChangesAsync(token);
    }
}

internal static class MaskingFluentExtensions
{
    // The fluent members answer IEventDataMasking, and ApplyAsync is on the class
    public static Task ApplyAsync(this IEventDataMasking masking)
        => ((EventDataMaskingBase<FakeSession>)masking).ApplyAsync();
}
