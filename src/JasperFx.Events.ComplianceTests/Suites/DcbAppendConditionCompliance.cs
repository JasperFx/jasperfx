using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

// Workshop seating: two tag types on one workshop, so two decisions can read one tag and append
// under the other -- the shape that separates "what a boundary read" from "what it appended".
public record WorkshopSeatsTag(Guid Value);

public record WorkshopWaitlistTag(Guid Value);

public record WorkshopSeatTaken(Guid MemberId);

public record WorkshopWaitlistJoined(Guid MemberId);

[BoundaryAggregate]
public class WorkshopSeating
{
    public int SeatsTaken { get; set; }
    public int Waitlisted { get; set; }

    public void Apply(WorkshopSeatTaken e) => SeatsTaken++;
    public void Apply(WorkshopWaitlistJoined e) => Waitlisted++;
}

// Library loans: one tag, several event types, for the conditions that name types
public record LoanTag(Guid Value);

public record LoanBookReturned(int DaysLate);

public record LoanReminderSent(int Number);

public record LoanExtended(int Days);

[BoundaryAggregate]
public class LoanState
{
    public bool Returned { get; set; }
    public int Reminders { get; set; }

    public void Apply(LoanBookReturned e) => Returned = true;
    public void Apply(LoanReminderSent e) => Reminders++;
}

// The dcb.events examples: a unique username, a gapless invoice number, course subscriptions
public record UsernameTag(string Value);

public record UsernameClaimed(string Username);

public record UsernameReleased(string Username);

public record UsernameLookedUp(string Username);

[BoundaryAggregate]
public class UsernameAvailability
{
    public bool Taken { get; set; }

    public void Apply(UsernameClaimed e) => Taken = true;
    public void Apply(UsernameReleased e) => Taken = false;
}

public record InvoiceSeriesTag(Guid Value);

public record InvoiceNumbered(int Number);

public record InvoiceSeriesViewed(string By);

[BoundaryAggregate]
public class InvoiceSeries
{
    public int Highest { get; set; }

    public void Apply(InvoiceNumbered e) => Highest = Math.Max(Highest, e.Number);
}

public record SubscriptionCourseTag(Guid Value);

public record SubscriptionStudentTag(Guid Value);

public record CourseSubscribed(Guid CourseId, Guid StudentId);

[BoundaryAggregate]
public class CourseSubscriptions
{
    public List<CourseSubscribed> Subscriptions { get; set; } = new();

    public void Apply(CourseSubscribed e) => Subscriptions.Add(e);
}

/// <summary>
/// The DCB append condition itself (marten#5887): a boundary's save fails if, and only if, an event
/// its query <em>matches</em> -- tag value and, where the condition names them, event type -- was
/// appended after the boundary read. Everything else must commit, concurrent saves must never
/// deadlock, and a save that loses writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DcbTagQueryAndConsistencyCompliance{TFixture,TOperations,TQuerySession}" /> pins that a
/// conflict is detected. This suite pins that it is detected <em>precisely</em>, and under real
/// concurrency, which is where every bug in the class has been. marten#5887 found two in one store at
/// once. Its boundary check bumped the version of every tag it read, so two boundaries reading one
/// tag always conflicted even when neither appended anything the other read. And the check and the
/// producer bumps took their row locks in two separately sorted passes, so two decisions that each
/// read the tag the other appended under deadlocked on every race.
/// </para>
/// <para>
/// The deadlock was invisible because the store's write retry committed the victim a second later.
/// That is why this suite sets <see cref="ComplianceStoreConfig.DisableWriteRetries" />, and why the
/// racing facts treat any failure other than a <see cref="DcbConcurrencyException" /> as a failure.
/// </para>
/// <para>
/// A condition that names event types is honoured. The DCB specification defines the append
/// condition by what the query matches, so an event of a type the condition does not select is not a
/// conflict even when it carries the condition's tag value. A store whose conflict check only looks
/// at tag values is conservative rather than unsafe -- it never misses a conflict, it reports false
/// ones -- but it fails the facts here that say an unselected type commits, and the raced username
/// fact, whose look-ups would otherwise knock out every claimant.
/// </para>
/// <para>
/// Left with the products, because they are storage rather than behaviour: the version rows a
/// check may or may not move, alternative tag storage layouts, and any rollout setting a store
/// offers for moving to type-scoped checks.
/// </para>
/// </remarks>
public abstract class DcbAppendConditionCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_dcb_append";

        // A retried deadlock commits, and then a deadlock and a queued lock look the same
        config.DisableWriteRetries = true;

        config.AddEventType<WorkshopSeatTaken>();
        config.AddEventType<WorkshopWaitlistJoined>();
        config.AddEventType<LoanBookReturned>();
        config.AddEventType<LoanReminderSent>();
        config.AddEventType<LoanExtended>();
        config.AddEventType<UsernameClaimed>();
        config.AddEventType<UsernameReleased>();
        config.AddEventType<UsernameLookedUp>();
        config.AddEventType<InvoiceNumbered>();
        config.AddEventType<InvoiceSeriesViewed>();
        config.AddEventType<CourseSubscribed>();

        // No aggregate association: every append here goes to an explicit stream or through a boundary
        config.RegisterTagType<WorkshopSeatsTag>("workshop_seats");
        config.RegisterTagType<WorkshopWaitlistTag>("workshop_waitlist");
        config.RegisterTagType<LoanTag>("loan");
        config.RegisterTagType<UsernameTag>("username");
        config.RegisterTagType<InvoiceSeriesTag>("invoice_series");
        config.RegisterTagType<SubscriptionCourseTag>("subscription_course");
        config.RegisterTagType<SubscriptionStudentTag>("subscription_student");
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private IEvent Tagged(TOperations session, object data, params object[] tags)
    {
        var e = EventsFor(session).BuildEvent(data);
        e.WithTag(tags);
        return e;
    }

    private async Task AppendElsewhereAsync(object data, params object[] tags)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(Guid.NewGuid(), Tagged(session, data, tags));
        await SaveChangesAsync(session);
    }

    private async Task<int> CountAsync(EventTagQuery query)
    {
        await using var session = OpenSession();
        return (await EventsFor(session).QueryByTagsAsync(query, Cancellation)).Count;
    }

    private static EventTagQuery SeatsOf(Guid workshop) => new EventTagQuery().Or(new WorkshopSeatsTag(workshop));

    private static EventTagQuery WaitlistOf(Guid workshop) =>
        new EventTagQuery().Or(new WorkshopWaitlistTag(workshop));

    private static EventTagQuery ReturnsOf(LoanTag loan) =>
        new EventTagQuery().Or(loan).AndEventsOfType<LoanBookReturned>();

    private static EventTagQuery RemindersOf(LoanTag loan) =>
        new EventTagQuery().Or(loan).AndEventsOfType<LoanReminderSent>();

    private async Task SeedSeatAsync(Guid workshop)
    {
        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);
        boundary.AppendOne(Tagged(session, new WorkshopSeatTaken(Guid.NewGuid()), new WorkshopSeatsTag(workshop)));
        await SaveChangesAsync(session);
    }

    #region Reading a tag is not writing it

    // marten#5887. Two boundaries read the same tag and each append an event carrying a tag the
    // other did not read. Neither append matches the other's query, so both commit. `seeded` covers
    // a tag that has events already and one that has none, which stores tend to handle on separate
    // paths (an existing version row versus creating one).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task two_boundaries_whose_appends_carry_no_tag_the_other_reads_both_commit(bool seeded)
    {
        var workshop = Guid.NewGuid();
        if (seeded) await SeedSeatAsync(workshop);

        await using var a = OpenSession();
        await using var b = OpenSession();
        var boundaryA = await EventsFor(a).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);
        var boundaryB = await EventsFor(b).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);

        boundaryA.AppendOne(Tagged(a, new WorkshopWaitlistJoined(Guid.NewGuid()), new WorkshopWaitlistTag(workshop)));
        boundaryB.AppendOne(Tagged(b, new WorkshopWaitlistJoined(Guid.NewGuid()), new WorkshopWaitlistTag(workshop)));

        await SaveChangesAsync(a);
        await Should.NotThrowAsync(() => SaveChangesAsync(b));

        (await CountAsync(WaitlistOf(workshop))).ShouldBe(2);
    }

    // ...and the precision cuts both ways: when A's append does carry the tag B read, B loses
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task an_append_matching_the_query_still_conflicts(bool seeded)
    {
        var workshop = Guid.NewGuid();
        if (seeded) await SeedSeatAsync(workshop);

        await using var a = OpenSession();
        await using var b = OpenSession();
        var boundaryA = await EventsFor(a).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);
        var boundaryB = await EventsFor(b).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);

        boundaryA.AppendOne(Tagged(a, new WorkshopSeatTaken(Guid.NewGuid()), new WorkshopSeatsTag(workshop)));
        boundaryB.AppendOne(Tagged(b, new WorkshopWaitlistJoined(Guid.NewGuid()), new WorkshopWaitlistTag(workshop)));

        await SaveChangesAsync(a);
        await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(b));
    }

    // The conflicting event arrives through a plain append rather than another boundary, and the
    // boundary's own append carries none of the tags its query reads -- so the conflict can only be
    // found by checking what was read, not by touching what is being written.
    [Fact]
    public async Task a_plain_append_matching_the_query_conflicts_with_a_boundary_appending_elsewhere()
    {
        var workshop = Guid.NewGuid();

        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);

        await AppendElsewhereAsync(new WorkshopSeatTaken(Guid.NewGuid()), new WorkshopSeatsTag(workshop));

        boundary.AppendOne(Tagged(session, new WorkshopWaitlistJoined(Guid.NewGuid()), new WorkshopWaitlistTag(workshop)));
        await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(session));
    }

    // The reporter's load shape in marten#5887: many concurrent decisions over one boundary, none
    // of which appends anything the others read. They may queue on a lock, and all of them commit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task concurrent_boundaries_whose_appends_the_others_do_not_read_all_commit(bool seeded)
    {
        const int racers = 16;

        for (var round = 0; round < 20; round++)
        {
            var workshop = Guid.NewGuid();
            if (seeded) await SeedSeatAsync(workshop);

            var gate = new AsyncGate(racers);
            var failures = await RaceAsync(racers, async () =>
            {
                await using var session = OpenSession();
                var boundary = await EventsFor(session)
                    .FetchForWritingByTags<WorkshopSeating>(SeatsOf(workshop), Cancellation);
                await gate.ArriveAndWaitAsync();
                boundary.AppendOne(Tagged(session, new WorkshopWaitlistJoined(Guid.NewGuid()),
                    new WorkshopWaitlistTag(workshop)));
                await SaveChangesAsync(session);
            });

            failures.ShouldBeEmpty();
            (await CountAsync(WaitlistOf(workshop))).ShouldBe(racers);
        }
    }

    // A reads the seats tag and appends under the waitlist tag; B reads the waitlist tag and appends
    // under the seats tag. Each append matches the other's query, so exactly one commits -- and the
    // other loses with a DcbConcurrencyException. Never a deadlock, never anything else.
    [Fact]
    public async Task crossed_boundaries_lose_with_a_dcb_conflict_not_a_deadlock()
    {
        for (var round = 0; round < 50; round++)
        {
            var workshop = Guid.NewGuid();
            var gate = new AsyncGate(2);

            var failures = await RaceAsync(
                () => DecideAsync(gate, SeatsOf(workshop),
                    s => Tagged(s, new WorkshopWaitlistJoined(Guid.NewGuid()), new WorkshopWaitlistTag(workshop))),
                () => DecideAsync(gate, WaitlistOf(workshop),
                    s => Tagged(s, new WorkshopSeatTaken(Guid.NewGuid()), new WorkshopSeatsTag(workshop))));

            failures.Count.ShouldBe(1);
            failures.ShouldAllBe(e => IsDcbConflict(e));
        }
    }

    private async Task DecideAsync(AsyncGate gate, EventTagQuery query, Func<TOperations, IEvent> append)
    {
        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<WorkshopSeating>(query, Cancellation);
        await gate.ArriveAndWaitAsync();
        boundary.AppendOne(append(session));
        await SaveChangesAsync(session);
    }

    #endregion

    #region What a condition matches

    // Happenstance E2E-06 / kraken-tech: a save is not checked against its own events. A boundary
    // appending several events its own query matches commits every one of them.
    [Fact]
    public async Task a_boundary_appending_events_its_own_query_matches_commits_them_all()
    {
        var series = new InvoiceSeriesTag(Guid.NewGuid());
        var query = new EventTagQuery().Or(series).AndEventsOfType<InvoiceNumbered>();

        await using (var session = OpenSession())
        {
            var boundary = await EventsFor(session).FetchForWritingByTags<InvoiceSeries>(query, Cancellation);
            boundary.AppendMany(
                Tagged(session, new InvoiceNumbered(1), series),
                Tagged(session, new InvoiceNumbered(2), series),
                Tagged(session, new InvoiceNumbered(3), series));
            await SaveChangesAsync(session);
        }

        (await CountAsync(query)).ShouldBe(3);
    }

    // Happenstance / kraken-tech / sliceworkz: a rejected append writes nothing. Both of the loser's
    // events are gone, including the one its condition did not select.
    [Fact]
    public async Task a_rejected_append_writes_nothing()
    {
        var series = new InvoiceSeriesTag(Guid.NewGuid());
        var query = new EventTagQuery().Or(series).AndEventsOfType<InvoiceNumbered>();

        await using var loser = OpenSession();
        var boundary = await EventsFor(loser).FetchForWritingByTags<InvoiceSeries>(query, Cancellation);

        await using (var winner = OpenSession())
        {
            var won = await EventsFor(winner).FetchForWritingByTags<InvoiceSeries>(query, Cancellation);
            won.AppendOne(Tagged(winner, new InvoiceNumbered(1), series));
            await SaveChangesAsync(winner);
        }

        boundary.AppendOne(Tagged(loser, new InvoiceNumbered(1), series));
        boundary.AppendOne(Tagged(loser, new InvoiceSeriesViewed("loser"), series));
        await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(loser));

        (await CountAsync(new EventTagQuery().Or(series))).ShouldBe(1);

        // ...and the loser's attempt left nothing behind that would fail the next honest decision
        await using var next = OpenSession();
        var retry = await EventsFor(next).FetchForWritingByTags<InvoiceSeries>(query, Cancellation);
        retry.Aggregate!.Highest.ShouldBe(1);
        retry.AppendOne(Tagged(next, new InvoiceNumbered(2), series));
        await Should.NotThrowAsync(() => SaveChangesAsync(next));
    }

    // Happenstance condition_with_an_unheld_tag_does_not_reject: an event of a selected type
    // carrying a DIFFERENT value of the condition's tag is not a match.
    [Fact]
    public async Task an_event_of_a_selected_type_with_another_tag_value_does_not_conflict()
    {
        var mine = new UsernameTag("alice-" + Guid.NewGuid());
        var other = new UsernameTag("bob-" + Guid.NewGuid());

        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<UsernameAvailability>(
            new EventTagQuery().Or(mine).AndEventsOfType<UsernameClaimed, UsernameReleased>(), Cancellation);

        await AppendElsewhereAsync(new UsernameClaimed(other.Value), other);

        boundary.AppendOne(Tagged(session, new UsernameClaimed(mine.Value), mine));
        await Should.NotThrowAsync(() => SaveChangesAsync(session));
    }

    // marten#5887's own repro: a reminder -- a type the decision does not fold -- lands on the loan
    // between the read and the save of a decision that selects only returns. Not a match.
    [Fact]
    public async Task an_event_of_a_type_the_condition_does_not_name_does_not_conflict()
    {
        var loan = new LoanTag(Guid.NewGuid());

        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(ReturnsOf(loan), Cancellation);

        await AppendElsewhereAsync(new LoanReminderSent(1), loan);

        boundary.AppendOne(Tagged(session, new LoanBookReturned(0), loan));
        await Should.NotThrowAsync(() => SaveChangesAsync(session));
    }

    [Fact]
    public async Task an_event_of_a_type_the_condition_names_conflicts()
    {
        var loan = new LoanTag(Guid.NewGuid());

        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(ReturnsOf(loan), Cancellation);

        await AppendElsewhereAsync(new LoanBookReturned(3), loan);

        boundary.AppendOne(Tagged(session, new LoanBookReturned(0), loan));
        await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(session));
    }

    [Fact]
    public async Task a_condition_naming_no_event_type_conflicts_with_any_type()
    {
        var loan = new LoanTag(Guid.NewGuid());

        await using var session = OpenSession();
        var boundary = await EventsFor(session)
            .FetchForWritingByTags<LoanState>(new EventTagQuery().Or(loan), Cancellation);

        await AppendElsewhereAsync(new LoanExtended(7), loan);

        boundary.AppendOne(Tagged(session, new LoanBookReturned(0), loan));
        await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(session));
    }

    // Several types in one condition: a later event of ANY of them conflicts, any other type does not
    [Fact]
    public async Task a_condition_naming_several_types_conflicts_with_each_of_them_only()
    {
        var loan = new LoanTag(Guid.NewGuid());
        var query = new EventTagQuery().Or(loan).AndEventsOfType<LoanBookReturned, LoanReminderSent>();

        await using (var session = OpenSession())
        {
            var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(query, Cancellation);
            await AppendElsewhereAsync(new LoanExtended(7), loan);
            boundary.AppendOne(Tagged(session, new LoanReminderSent(1), loan));
            await Should.NotThrowAsync(() => SaveChangesAsync(session));
        }

        await using (var session = OpenSession())
        {
            var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(query, Cancellation);
            await AppendElsewhereAsync(new LoanReminderSent(2), loan);
            boundary.AppendOne(Tagged(session, new LoanBookReturned(1), loan));
            await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(session));
        }
    }

    // A store that narrows the CHECK to the selected types must not narrow the READ past them, or
    // widen it: the decision folds every selected event and nothing else.
    [Fact]
    public async Task the_boundary_reads_exactly_the_events_its_condition_selects()
    {
        var loan = new LoanTag(Guid.NewGuid());
        await AppendElsewhereAsync(new LoanReminderSent(1), loan);
        await AppendElsewhereAsync(new LoanReminderSent(2), loan);
        await AppendElsewhereAsync(new LoanExtended(7), loan);

        await using var session = OpenSession();
        var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(RemindersOf(loan), Cancellation);

        boundary.Aggregate!.Reminders.ShouldBe(2);
        boundary.Events.Count.ShouldBe(2);
    }

    // wwwision/dcb-eventstore-doctrine conditional_append_fails_if_an_event_was_in_flight_during_the_decision.
    // Writer A appends a matching event -- taking the LOWER sequence -- and holds its transaction open.
    // Writer B appends and commits a matching event above it. The decision reads, seeing B and not A,
    // and then A commits. The decision never saw A's event, so its append must fail. A store that
    // checks "anything matching above the highest position I read" misses this one.
    //
    // A store whose reads take shared locks (SQL Server's default READ COMMITTED) never reaches that
    // state: the decision's read waits for A to commit and then sees A's event, which is just as
    // correct. So the read runs concurrently with the hold, A is released once the read has finished
    // or has visibly blocked, and the assertion is the one both kinds of store owe: the decision
    // never commits having missed A's event.
    [Fact]
    public async Task an_event_in_flight_below_what_the_decision_read_still_conflicts()
    {
        Assert.SkipUnless(theFixture.SupportsHoldingAnAppendOpen,
            "This store cannot hold an append open, so it has no in-flight appends to miss");

        var course = new SubscriptionCourseTag(Guid.NewGuid());
        var student = new SubscriptionStudentTag(Guid.NewGuid());

        // The decision reads both tags, and A and B each carry a different one, so B's commit does not
        // queue behind whatever A's open transaction holds for its own tag
        var query = new EventTagQuery().Or(course).Or(student);

        IEvent held;
        await using (var builder = OpenSession())
        {
            held = Tagged(builder, new CourseSubscribed(course.Value, Guid.NewGuid()), course);
        }

        IAsyncDisposable? inFlight = await theFixture.HoldAppendOpenAsync(Guid.NewGuid(), held, Cancellation);
        try
        {
            await AppendElsewhereAsync(new CourseSubscribed(Guid.NewGuid(), student.Value), student);

            await using var decision = OpenSession();
            var reading = EventsFor(decision).FetchForWritingByTags<CourseSubscriptions>(query, Cancellation);

            // A snapshot read returns at once; a locking one is still waiting on A after this
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(3), Cancellation));

            await inFlight.DisposeAsync();
            inFlight = null;

            var boundary = await reading;
            boundary.AppendOne(Tagged(decision, new CourseSubscribed(course.Value, student.Value), course, student));

            if (boundary.Events.Count == 1)
            {
                // Read B only -- A was invisible, so the save has to find A's event
                await ShouldFailWithAsync<DcbConcurrencyException>(() => SaveChangesAsync(decision));
            }
            else
            {
                // The read waited for A and saw both, so nothing it missed can conflict
                boundary.Events.Count.ShouldBe(2);
                await Should.NotThrowAsync(() => SaveChangesAsync(decision));
            }
        }
        finally
        {
            if (inFlight != null) await inFlight.DisposeAsync();
        }
    }

    #endregion

    #region Racing decisions

    // Happenstance k_disjoint_boundaries_never_conflict / Axon concurrentTransactionsForNonOverlappingTags
    [Fact]
    public async Task disjoint_boundaries_in_parallel_never_conflict()
    {
        const int boundaries = 24;

        for (var round = 0; round < 10; round++)
        {
            var failures = await RaceAsync(boundaries, async () =>
            {
                var series = new InvoiceSeriesTag(Guid.NewGuid());
                await using var session = OpenSession();
                var boundary = await EventsFor(session).FetchForWritingByTags<InvoiceSeries>(
                    new EventTagQuery().Or(series).AndEventsOfType<InvoiceNumbered>(), Cancellation);
                boundary.AppendOne(Tagged(session, new InvoiceNumbered(1), series));
                await SaveChangesAsync(session);
            });

            failures.ShouldBeEmpty();
        }
    }

    // Many decisions over one loan each select returns and append a reminder. None appends what the
    // others select, so every one commits.
    [Fact]
    public async Task concurrent_decisions_appending_types_the_others_do_not_select_all_commit()
    {
        const int racers = 16;

        for (var round = 0; round < 20; round++)
        {
            var loan = new LoanTag(Guid.NewGuid());
            await AppendElsewhereAsync(new LoanBookReturned(0), loan);

            var gate = new AsyncGate(racers);
            var failures = await RaceAsync(racers, async () =>
            {
                await using var session = OpenSession();
                var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(ReturnsOf(loan), Cancellation);
                await gate.ArriveAndWaitAsync();
                boundary.AppendOne(Tagged(session, new LoanReminderSent(1), loan));
                await SaveChangesAsync(session);
            });

            failures.ShouldBeEmpty();
        }
    }

    // Exactly one of N decisions appending the type they all select may commit
    [Fact]
    public async Task concurrent_decisions_appending_the_type_they_select_elect_one_winner()
    {
        const int racers = 16;

        for (var round = 0; round < 20; round++)
        {
            var loan = new LoanTag(Guid.NewGuid());
            var gate = new AsyncGate(racers);
            var failures = await RaceAsync(racers, async () =>
            {
                await using var session = OpenSession();
                var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(ReturnsOf(loan), Cancellation);
                await gate.ArriveAndWaitAsync();
                boundary.AppendOne(Tagged(session, new LoanBookReturned(0), loan));
                await SaveChangesAsync(session);
            });

            failures.Count.ShouldBe(racers - 1);
            failures.ShouldAllBe(e => IsDcbConflict(e));
        }
    }

    // Type-scoped conditions make crossed decisions routine on ONE tag value: one selects returns and
    // appends a reminder, the other selects reminders and appends a return. One loses, and only
    // ever with a DcbConcurrencyException.
    [Fact]
    public async Task decisions_crossed_by_event_type_lose_with_a_dcb_conflict_not_a_deadlock()
    {
        for (var round = 0; round < 50; round++)
        {
            var loan = new LoanTag(Guid.NewGuid());
            var gate = new AsyncGate(2);

            async Task Decide(EventTagQuery query, Func<TOperations, IEvent> append)
            {
                await using var session = OpenSession();
                var boundary = await EventsFor(session).FetchForWritingByTags<LoanState>(query, Cancellation);
                await gate.ArriveAndWaitAsync();
                boundary.AppendOne(append(session));
                await SaveChangesAsync(session);
            }

            var failures = await RaceAsync(
                () => Decide(ReturnsOf(loan), s => Tagged(s, new LoanReminderSent(1), loan)),
                () => Decide(RemindersOf(loan), s => Tagged(s, new LoanBookReturned(0), loan)));

            failures.Count.ShouldBe(1);
            failures.ShouldAllBe(e => IsDcbConflict(e));
        }
    }

    // dcb.events "unique username", raced. One tag value, two selected types, and look-ups -- an
    // UNSELECTED type on the same tag value -- landing throughout. Exactly one claim wins, and nothing
    // but a DcbConcurrencyException is ever thrown. A store that ignored the condition's types would
    // let look-ups knock out claimants, and could end a round with no claim at all.
    [Fact]
    public async Task a_raced_username_is_claimed_exactly_once()
    {
        const int claimants = 12;

        for (var round = 0; round < 15; round++)
        {
            var username = new UsernameTag("user-" + Guid.NewGuid());
            var query = new EventTagQuery().Or(username).AndEventsOfType<UsernameClaimed, UsernameReleased>();
            var gate = new AsyncGate(claimants + 1);

            var lookUps = RaceAsync(1, async () =>
            {
                await gate.ArriveAndWaitAsync();
                for (var i = 0; i < 5; i++)
                {
                    await AppendElsewhereAsync(new UsernameLookedUp(username.Value), username);
                }
            });

            var claims = RaceAsync(claimants, async () =>
            {
                await using var session = OpenSession();
                var boundary = await EventsFor(session).FetchForWritingByTags<UsernameAvailability>(query, Cancellation);
                await gate.ArriveAndWaitAsync();
                if (boundary.Aggregate is { Taken: true }) return;

                boundary.AppendOne(Tagged(session, new UsernameClaimed(username.Value), username));
                await SaveChangesAsync(session);
            });

            (await lookUps).ShouldBeEmpty();
            (await claims).ShouldAllBe(e => IsDcbConflict(e));

            (await CountAsync(new EventTagQuery().Or(username).AndEventsOfType<UsernameClaimed>())).ShouldBe(1);
        }
    }

    // dcb.events "invoice number" as a randomized invariant: concurrent allocators each read the
    // series, take the next number and retry on conflict, while events of another type keep landing
    // on the same series. The committed numbers are exactly 1..N -- no gap, no duplicate.
    [Fact]
    public async Task concurrently_allocated_invoice_numbers_are_gapless_and_unique()
    {
        var series = new InvoiceSeriesTag(Guid.NewGuid());
        var query = new EventTagQuery().Or(series).AndEventsOfType<InvoiceNumbered>();
        const int allocators = 10;
        const int invoicesEach = 5;
        using var stop = new CancellationTokenSource();

        var noise = RaceAsync(1, async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await AppendElsewhereAsync(new InvoiceSeriesViewed("auditor"), series);

                // Throttled so a store whose check ignores event types still gets allocations through:
                // every view would otherwise invalidate every open read of the series
                await Task.Delay(5);
            }
        });

        var allocations = RaceAsync(allocators, async () =>
        {
            for (var invoice = 0; invoice < invoicesEach; invoice++)
            {
                for (var attempt = 0;; attempt++)
                {
                    try
                    {
                        await using var session = OpenSession();
                        var boundary = await EventsFor(session).FetchForWritingByTags<InvoiceSeries>(query, Cancellation);
                        var next = (boundary.Aggregate?.Highest ?? 0) + 1;
                        boundary.AppendOne(Tagged(session, new InvoiceNumbered(next), series));
                        await SaveChangesAsync(session);
                        break;
                    }
                    catch (Exception e) when (IsDcbConflict(e) && attempt < 500)
                    {
                    }
                }
            }
        });

        var allocationFailures = await allocations;
        stop.Cancel();
        (await noise).ShouldBeEmpty();
        allocationFailures.ShouldBeEmpty();

        await using var check = OpenSession();
        var numbers = (await EventsFor(check).QueryByTagsAsync(query, Cancellation))
            .Select(e => ((InvoiceNumbered)e.Data).Number).OrderBy(x => x).ToArray();
        numbers.ShouldBe(Enumerable.Range(1, allocators * invoicesEach).ToArray());
    }

    // dcb.events "course subscriptions" as a randomized invariant: a two-condition query (the course's
    // subscriptions OR the student's), a course capacity and a per-student limit, raced across a small
    // grid so decisions overlap on both tags. Neither limit is ever exceeded.
    [Fact]
    public async Task raced_course_subscriptions_respect_both_limits()
    {
        const int courseCapacity = 3;
        const int studentLimit = 2;
        var courses = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var students = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var next = -1;

        var failures = await RaceAsync(48, async () =>
        {
            var i = Interlocked.Increment(ref next);
            var course = courses[Random.Shared.Next(courses.Length)];
            var student = students[i % students.Length];
            var query = new EventTagQuery()
                .Or(new SubscriptionCourseTag(course)).AndEventsOfType<CourseSubscribed>()
                .Or(new SubscriptionStudentTag(student)).AndEventsOfType<CourseSubscribed>();

            await using var session = OpenSession();
            var boundary = await EventsFor(session).FetchForWritingByTags<CourseSubscriptions>(query, Cancellation);
            var subscriptions = boundary.Aggregate?.Subscriptions ?? new List<CourseSubscribed>();

            if (subscriptions.Count(x => x.CourseId == course) >= courseCapacity) return;
            if (subscriptions.Count(x => x.StudentId == student) >= studentLimit) return;
            if (subscriptions.Any(x => x.CourseId == course && x.StudentId == student)) return;

            boundary.AppendOne(Tagged(session, new CourseSubscribed(course, student),
                new SubscriptionCourseTag(course), new SubscriptionStudentTag(student)));
            await SaveChangesAsync(session);
        });

        failures.ShouldAllBe(e => IsDcbConflict(e));

        await using var check = OpenSession();
        var all = (await EventsFor(check).QueryByTagsAsync(
                courses.Aggregate(new EventTagQuery(), (q, c) => q.Or(new SubscriptionCourseTag(c))), Cancellation))
            .Select(e => (CourseSubscribed)e.Data).ToList();

        all.ShouldNotBeEmpty();
        all.GroupBy(x => x.CourseId).ShouldAllBe(g => g.Count() <= courseCapacity);
        all.GroupBy(x => x.StudentId).ShouldAllBe(g => g.Count() <= studentLimit);
        all.GroupBy(x => (x.CourseId, x.StudentId)).ShouldAllBe(g => g.Count() == 1);
    }

    #endregion

    /// <summary>
    /// A DCB conflict, thrown directly or inside an <see cref="AggregateException" /> -- the same
    /// tolerance <see cref="EventStoreComplianceSuite{TFixture,TOperations,TQuerySession}.ShouldFailWithAsync{TException}" />
    /// has, for a store that runs its unit of work in parallel.
    /// </summary>
    private static bool IsDcbConflict(Exception e) =>
        e is DcbConcurrencyException ||
        (e is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(x => x is DcbConcurrencyException));

    private static Task<IReadOnlyList<Exception>> RaceAsync(int count, Func<Task> body)
        => RaceAsync(Enumerable.Range(0, count).Select(_ => body).ToArray());

    /// <summary>
    /// Run every body on its own thread-pool task and collect what each one threw. A racing fact
    /// asserts on the collection, so one racer's failure never hides another's.
    /// </summary>
    private static async Task<IReadOnlyList<Exception>> RaceAsync(params Func<Task>[] bodies)
    {
        var failures = new ConcurrentBag<Exception>();

        await Task.WhenAll(bodies.Select(body => Task.Run(async () =>
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }))).ConfigureAwait(false);

        return failures.ToArray();
    }

    /// <summary>
    /// Lets every racer finish its read before any of them saves, without parking thread-pool threads.
    /// </summary>
    private sealed class AsyncGate(int count)
    {
        private int _arrived;
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == count) _open.TrySetResult();
            return _open.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
