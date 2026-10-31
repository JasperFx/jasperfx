using EventTests.Operations;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Appending;
using JasperFx.MultiTenancy;
using Shouldly;

namespace EventTests.Appending;

public class SessionMetadata : IMetadataContext
{
    public string TenantId { get; set; } = "tenant-a";
    public string? CausationId { get; set; } = "session-causation";
    public string? CorrelationId { get; set; } = "session-correlation";
    public string? CurrentUserName { get; set; } = "session-user";
    public Dictionary<string, object>? Headers { get; set; } = new() { ["session-header"] = "session-value" };
    public bool CausationIdEnabled { get; set; } = true;
    public bool CorrelationIdEnabled { get; set; } = true;
    public bool HeadersEnabled { get; set; } = true;
    public bool UserNameEnabled { get; set; } = true;
}

internal static class Planning
{
    public static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public static ClientSideAppendContext Context(IMetadataContext? metadata = null,
        EventAppendMode mode = EventAppendMode.Quick, TenancyStyle tenancy = TenancyStyle.Conjoined,
        AppendOrigin origin = AppendOrigin.UnitOfWork, IReadOnlyList<long>? sequences = null,
        AppendRefusals? refusals = null)
        => new()
        {
            Metadata = metadata ?? new SessionMetadata(),
            Timestamp = Now,
            AppendMode = mode,
            Tenancy = tenancy,
            Origin = origin,
            ReservedSequences = sequences,
            Refusals = refusals ?? AppendRefusals.Shared
        };

    public static IEvent NewEvent(string name = "someone") => new Event<MemberJoined>(new MemberJoined(name));

    public static StreamAction Action(StreamActionType type = StreamActionType.Append, int events = 2,
        Guid? id = null, string tenant = "tenant-a")
    {
        var action = new StreamAction(id ?? Guid.NewGuid(), type) { TenantId = tenant };
        for (var i = 0; i < events; i++) action.AddEvent(NewEvent());

        return action;
    }

    public static ClientSideAppendPlan Plan(StreamAction stream, StreamRowSnapshot row,
        ClientSideAppendContext? context = null)
        => ClientSideAppendPlanner.Plan([stream], _ => row, context ?? Context());
}

/// <summary>
///     The decision table of jasperfx#1051, tested against every combination it can be asked about. The
///     combinations come from the enums and not from the table, and the answers come from
///     <see cref="Oracle" /> and not from the table, so a rule that goes missing, a rule that changes, and a
///     new member of any enum are all failures rather than gaps.
/// </summary>
public class ClientSideAppendDecisionTable
{
    private const long CurrentVersion = 5;

    /// <summary>
    ///     The answers, written out apart from the production table on purpose. Nothing here reads
    ///     <see cref="ClientSideAppendPlanner.Rules" />, and the discard arm is what makes a new enum member
    ///     fail rather than fall through.
    /// </summary>
    private static StreamAppendVerdict Oracle(StreamAppendWork work, StreamRowState row, ExpectedVersionCheck expected)
        => (work, row, expected) switch
        {
            (StreamAppendWork.Nothing, _, _) => StreamAppendVerdict.Skip,

            (StreamAppendWork.MixedTenants, _, _) => StreamAppendVerdict.RefuseMixedTenants,

            (StreamAppendWork.VerifyVersion, StreamRowState.NotRead, _) => StreamAppendVerdict.RefuseUnreadRow,
            (StreamAppendWork.VerifyVersion, _, ExpectedVersionCheck.Differs) => StreamAppendVerdict.RefuseUnexpectedVersion,
            (StreamAppendWork.VerifyVersion, _, _) => StreamAppendVerdict.Verified,

            // Collision wins over everything: an archived id is an id in use, and a version is beside the point
            (StreamAppendWork.Start, StreamRowState.Live or StreamRowState.Archived, _) => StreamAppendVerdict.RefuseCollision,
            (StreamAppendWork.Start, _, ExpectedVersionCheck.Differs) => StreamAppendVerdict.RefuseUnexpectedVersion,
            (StreamAppendWork.Start, StreamRowState.Missing or StreamRowState.NotRead, _) => StreamAppendVerdict.InsertStream,

            (StreamAppendWork.Append, StreamRowState.NotRead, _) => StreamAppendVerdict.RefuseUnreadRow,

            // Archived wins over the version: "this stream is closed" is the more specific answer
            (StreamAppendWork.Append, StreamRowState.Archived, _) => StreamAppendVerdict.RefuseArchived,
            (StreamAppendWork.Append, _, ExpectedVersionCheck.Differs) => StreamAppendVerdict.RefuseUnexpectedVersion,
            (StreamAppendWork.Append, StreamRowState.Missing, _) => StreamAppendVerdict.InsertStream,
            (StreamAppendWork.Append, StreamRowState.Live, _) => StreamAppendVerdict.UpdateStream,

            _ => throw new InvalidOperationException($"The oracle has no answer for {work}, {row}, {expected}")
        };

    public static IEnumerable<TheoryDataRow<StreamAppendWork, StreamRowState, ExpectedVersionCheck>> Cells()
    {
        foreach (var work in Enum.GetValues<StreamAppendWork>())
        {
            foreach (var row in Enum.GetValues<StreamRowState>())
            {
                foreach (var expected in Enum.GetValues<ExpectedVersionCheck>())
                {
                    yield return new(work, row, expected);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void exactly_one_rule_covers_each_combination(StreamAppendWork work, StreamRowState row,
        ExpectedVersionCheck expected)
    {
        var covering = ClientSideAppendPlanner.Rules.Where(x => x.Covers(work, row, expected)).ToArray();

        covering.Length.ShouldBe(1, $"{work}, {row}, {expected}: {string.Join("; ", covering.Select(x => x.Why))}");
        covering[0].Verdict.ShouldBe(Oracle(work, row, expected));
    }

    [Fact]
    public void every_rule_says_why()
    {
        ClientSideAppendPlanner.Rules.ShouldAllBe(x => !string.IsNullOrWhiteSpace(x.Why));
    }

    [Fact]
    public void every_rule_is_reachable()
    {
        foreach (var rule in ClientSideAppendPlanner.Rules)
        {
            var reached = Cells().Any(cell =>
            {
                var (work, row, expected) = cell.Data;
                return ReferenceEquals(ClientSideAppendPlanner.Decide(work, row, expected), rule);
            });

            reached.ShouldBeTrue(rule.Why);
        }
    }

    /// <summary>
    ///     Every combination again, this time as real inputs through <see cref="ClientSideAppendPlanner.Plan" />
    ///     and across the dimensions that must not change the decision, checking what each one is allowed to
    ///     change instead. The single combination <see cref="WorkFor" /> cannot produce has its own test below.
    /// </summary>
    public static IEnumerable<TheoryDataRow<StreamAppendWork, StreamRowState, ExpectedVersionCheck, EventAppendMode,
        bool, StreamIdentity, AppendOrigin>> Scenarios()
    {
        foreach (var cell in Cells())
        {
            var (work, row, expected) = cell.Data;
            if (work == StreamAppendWork.VerifyVersion && expected == ExpectedVersionCheck.None) continue;

            foreach (var mode in Enum.GetValues<EventAppendMode>())
            {
                foreach (var withStreamType in new[] { true, false })
                {
                    foreach (var identity in Enum.GetValues<StreamIdentity>())
                    {
                        foreach (var origin in Enum.GetValues<AppendOrigin>())
                        {
                            yield return new(work, row, expected, mode, withStreamType, identity, origin);
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void every_scenario_plans_as_the_table_says(StreamAppendWork work, StreamRowState rowState,
        ExpectedVersionCheck expected, EventAppendMode mode, bool withStreamType, StreamIdentity identity,
        AppendOrigin origin)
    {
        var row = rowState switch
        {
            StreamRowState.NotRead => StreamRowSnapshot.NotRead,
            StreamRowState.Missing => StreamRowSnapshot.Missing,
            StreamRowState.Live => StreamRowSnapshot.Live(CurrentVersion),
            StreamRowState.Archived => StreamRowSnapshot.Archived(CurrentVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(rowState))
        };

        var type = work == StreamAppendWork.Start ? StreamActionType.Start : StreamActionType.Append;
        var stream = identity == StreamIdentity.AsGuid
            ? new StreamAction(Guid.NewGuid(), type)
            : new StreamAction(Guid.NewGuid().ToString(), type);
        stream.TenantId = "tenant-a";

        if (work is StreamAppendWork.Start or StreamAppendWork.Append or StreamAppendWork.MixedTenants)
        {
            stream.AddEvent(Planning.NewEvent("first"));
            stream.AddEvent(Planning.NewEvent("second"));
        }

        if (work == StreamAppendWork.MixedTenants) stream.Events[1].TenantId = "tenant-b";

        stream.AggregateType = withStreamType ? typeof(Quest) : null;
        stream.AlwaysEnforceConsistency = work == StreamAppendWork.VerifyVersion;
        stream.ExpectedVersionOnServer = expected switch
        {
            ExpectedVersionCheck.None => null,
            ExpectedVersionCheck.Matches => row.Version,
            ExpectedVersionCheck.Differs => row.Version + 3,
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };

        var id = identity == StreamIdentity.AsGuid ? (object)stream.Id : stream.Key!;
        var expectedVersionBefore = stream.ExpectedVersionOnServer;

        ClientSideAppendPlanner.WorkFor(stream, TenancyStyle.Conjoined).ShouldBe(work);

        var plan = ClientSideAppendPlanner.Plan([stream], _ => row, Planning.Context(mode: mode, origin: origin,
            sequences: [101, 102]));

        // A raised event's expected version is a guess, so whatever it was, nothing is compared
        var compared = origin == AppendOrigin.RaisedByProjection ? ExpectedVersionCheck.None : expected;
        var verdict = Oracle(work, rowState, compared);

        var step = plan.Steps.ShouldHaveSingleItem();
        step.Verdict.ShouldBe(verdict);
        step.CurrentVersion.ShouldBe(work == StreamAppendWork.Nothing ? 0 : row.Version);
        step.Stream.ShouldBeSameAs(stream);

        // Planning is pure, whatever it decided
        assertUntouched(stream, expectedVersionBefore);

        switch (verdict)
        {
            case StreamAppendVerdict.Skip or StreamAppendVerdict.Verified:
                plan.Refusal.ShouldBeNull();
                plan.Writes.ShouldBeEmpty();
                plan.Apply();
                assertUntouched(stream, expectedVersionBefore);
                break;

            case StreamAppendVerdict.InsertStream or StreamAppendVerdict.UpdateStream:
                plan.Refusal.ShouldBeNull();
                plan.Writes.ShouldHaveSingleItem().ShouldBeSameAs(step);
                plan.Apply();

                stream.Events.Select(x => x.Version).ShouldBe([row.Version + 1, row.Version + 2]);
                stream.Version.ShouldBe(row.Version + 2);
                step.EndingVersion.ShouldBe(row.Version + 2);

                stream.ExpectedVersionOnServer.ShouldBe(verdict == StreamAppendVerdict.UpdateStream
                    ? row.Version
                    : origin == AppendOrigin.RaisedByProjection ? null : expectedVersionBefore);

                stream.Events.ShouldAllBe(x => x.Timestamp == Planning.Now);
                stream.AggregateType.ShouldBe(withStreamType ? typeof(Quest) : null);

                // Who supplies the sequence is the one thing the append mode changes
                stream.Events.Select(x => x.Sequence).ShouldBe(mode == EventAppendMode.Rich ? [101, 102] : [0, 0]);
                break;

            default:
                plan.Refusal.ShouldBeSameAs(step);
                plan.Writes.ShouldBeEmpty();

                var thrown = Should.Throw<Exception>(() => plan.Apply());
                assertRefusal(thrown, verdict, id, withStreamType ? typeof(Quest) : null, expectedVersionBefore,
                    row.Version);

                assertUntouched(stream, expectedVersionBefore);
                break;
        }
    }

    private static void assertRefusal(Exception thrown, StreamAppendVerdict verdict, object id, Type? streamType,
        long? expected, long actual)
    {
        switch (verdict)
        {
            case StreamAppendVerdict.RefuseCollision:
                var collision = thrown.ShouldBeOfType<ExistingStreamIdCollisionException>();
                collision.Id.ShouldBe(id);
                collision.AggregateType.ShouldBe(streamType);
                break;

            case StreamAppendVerdict.RefuseArchived:
                thrown.ShouldBeOfType<ArchivedStreamException>().Id.ShouldBe(id);
                break;

            case StreamAppendVerdict.RefuseUnexpectedVersion:
                var unexpected = thrown.ShouldBeOfType<EventStreamUnexpectedMaxEventIdException>();
                unexpected.Id.ShouldBe(id);
                unexpected.AggregateType.ShouldBe(streamType);
                unexpected.Message.ShouldContain($"expected {expected} but was {actual}");
                break;

            case StreamAppendVerdict.RefuseMixedTenants:
                thrown.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("another tenant");
                break;

            case StreamAppendVerdict.RefuseUnreadRow:
                thrown.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("did not read");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a refusal");
        }
    }

    private static void assertUntouched(StreamAction stream, long? expected)
    {
        stream.ExpectedVersionOnServer.ShouldBe(expected);
        stream.Version.ShouldBe(0);

        foreach (var @event in stream.Events)
        {
            @event.Version.ShouldBe(0);
            @event.Sequence.ShouldBe(0);
            @event.Timestamp.ShouldBe(default);
            @event.CorrelationId.ShouldBeNull();
            @event.Headers.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(StreamActionType.Start)]
    [InlineData(StreamActionType.Append)]
    public void asking_for_consistency_with_nothing_to_compare_asks_for_nothing(StreamActionType type)
    {
        var stream = Planning.Action(type, events: 0);
        stream.AlwaysEnforceConsistency = true;

        ClientSideAppendPlanner.WorkFor(stream, TenancyStyle.Single).ShouldBe(StreamAppendWork.Nothing);
        Planning.Plan(stream, StreamRowSnapshot.Live(9)).Steps[0].Verdict.ShouldBe(StreamAppendVerdict.Skip);
    }

    [Theory]
    [InlineData(StreamActionType.Start)]
    [InlineData(StreamActionType.Append)]
    public void an_action_with_no_events_is_never_a_write_whatever_its_type(StreamActionType type)
    {
        var stream = Planning.Action(type, events: 0);
        stream.ExpectedVersionOnServer = 3;
        stream.AlwaysEnforceConsistency = true;

        ClientSideAppendPlanner.WorkFor(stream, TenancyStyle.Single).ShouldBe(StreamAppendWork.VerifyVersion);
        Planning.Plan(stream, StreamRowSnapshot.Live(3)).Writes.ShouldBeEmpty();
    }

    [Fact]
    public void a_stream_that_exists_at_version_zero_exists()
    {
        var plan = Planning.Plan(Planning.Action(), StreamRowSnapshot.Live(0));

        plan.Steps[0].Verdict.ShouldBe(StreamAppendVerdict.UpdateStream);
    }
}

public class ClientSideAppendSnapshots
{
    [Fact]
    public void the_default_is_not_a_missing_stream()
    {
        default(StreamRowSnapshot).State.ShouldBe(StreamRowState.NotRead);

        // What a lookup that forgets a stream hands back. A missing row here would restart it at version 1.
        var plan = Planning.Plan(Planning.Action(), default);

        plan.Refusal.ShouldNotBeNull().Verdict.ShouldBe(StreamAppendVerdict.RefuseUnreadRow);
    }

    [Theory]
    [InlineData(null, false, StreamRowState.Missing, 0L)]
    [InlineData(null, true, StreamRowState.Missing, 0L)]
    [InlineData(7L, false, StreamRowState.Live, 7L)]
    [InlineData(7L, true, StreamRowState.Archived, 7L)]
    public void from_a_row_as_the_stores_read_it(long? version, bool archived, StreamRowState state, long resulting)
    {
        var snapshot = StreamRowSnapshot.FromRow(version, archived);

        snapshot.State.ShouldBe(state);
        snapshot.Version.ShouldBe(resulting);
    }
}

/// <summary>
///     Polecat's and Fisher's numbering and metadata disagreed, and these are the answers jasperfx#1051
///     picked. A test here that would pass on Polecat's <c>AssignEventMetadataForClosedShape</c> is not
///     testing the drift.
/// </summary>
public class ClientSideAppendMetadata
{
    [Fact]
    public void every_event_of_a_unit_of_work_gets_the_supplied_moment_and_not_a_clock_reading()
    {
        var stream = Planning.Action(events: 3);

        var plan = Planning.Plan(stream, StreamRowSnapshot.Live(1));
        plan.Apply();

        // Polecat read DateTimeOffset.UtcNow per event, then wrote SYSDATETIMEOFFSET() to the row, so what
        // an inline projection folded and what a rebuild reads never agreed
        stream.Events.Select(x => x.Timestamp).Distinct().ShouldHaveSingleItem().ShouldBe(Planning.Now);
    }

    [Fact]
    public void a_timestamp_the_caller_set_survives()
    {
        var stream = Planning.Action();
        var earlier = Planning.Now.AddHours(-1);
        stream.Events[0].Timestamp = earlier;

        var plan = Planning.Plan(stream, StreamRowSnapshot.Live(1));
        plan.Apply();

        stream.Events[0].Timestamp.ShouldBe(earlier);
        stream.Events[1].Timestamp.ShouldBe(Planning.Now);
    }

    [Theory]
    [InlineData(EventAppendMode.Rich)]
    [InlineData(EventAppendMode.Quick)]
    [InlineData(EventAppendMode.QuickWithServerTimestamps)]
    public void every_mode_stamps_the_client_moment_for_the_store_to_persist(EventAppendMode mode)
    {
        var stream = Planning.Action(events: 1);

        Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(mode: mode, sequences: [1])).Apply();

        stream.Events[0].Timestamp.ShouldBe(Planning.Now);
    }

    public static IEnumerable<TheoryDataRow<bool, bool, bool, bool>> Switches()
    {
        foreach (var correlation in new[] { true, false })
        foreach (var causation in new[] { true, false })
        foreach (var user in new[] { true, false })
        foreach (var headers in new[] { true, false })
        {
            yield return new(correlation, causation, user, headers);
        }
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public void each_field_is_gated_on_its_own_switch(bool correlation, bool causation, bool user, bool headers)
    {
        var session = new SessionMetadata
        {
            CorrelationIdEnabled = correlation,
            CausationIdEnabled = causation,
            UserNameEnabled = user,
            HeadersEnabled = headers
        };

        var stream = Planning.Action(events: 1);

        Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(session)).Apply();

        // A disabled column is not written, so stamping the envelope would show an inline projection
        // something a rebuild of the same event cannot find. Polecat stamped all four regardless.
        var @event = stream.Events[0];
        @event.CorrelationId.ShouldBe(correlation ? "session-correlation" : null);
        @event.CausationId.ShouldBe(causation ? "session-causation" : null);
        @event.UserName.ShouldBe(user ? "session-user" : null);
        @event.GetHeader("session-header").ShouldBe(headers ? "session-value" : null);
    }

    [Fact]
    public void what_an_event_already_carries_is_left_alone()
    {
        var stream = Planning.Action(events: 1);
        var @event = stream.Events[0];
        @event.CorrelationId = "own-correlation";
        @event.CausationId = "own-causation";
        @event.UserName = "own-user";
        @event.SetHeader("session-header", "own-value");

        Planning.Plan(stream, StreamRowSnapshot.Live(1)).Apply();

        @event.CorrelationId.ShouldBe("own-correlation");
        @event.CausationId.ShouldBe("own-causation");
        @event.UserName.ShouldBe("own-user");

        // Fisher's SetHeader replaced this, against its own summary. Polecat's TryAdd did not.
        @event.GetHeader("session-header").ShouldBe("own-value");
    }

    [Fact]
    public void no_session_headers_leaves_no_empty_dictionary_to_serialize()
    {
        var stream = Planning.Action(events: 1);

        Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(new SessionMetadata { Headers = [] }))
            .Apply();

        stream.Events[0].Headers.ShouldBeNull();
    }

    [Fact]
    public void events_do_not_share_the_sessions_header_dictionary()
    {
        var stream = Planning.Action();

        Planning.Plan(stream, StreamRowSnapshot.Live(1)).Apply();

        stream.Events[0].Headers.ShouldNotBeSameAs(stream.Events[1].Headers);
    }

    [Fact]
    public void an_event_with_no_tenant_takes_the_streams()
    {
        var stream = Planning.Action(events: 1);
        stream.Events[0].TenantId = null!;

        Planning.Plan(stream, StreamRowSnapshot.Live(1)).Apply();

        stream.Events[0].TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public void an_event_with_no_id_gets_one()
    {
        var stream = Planning.Action(events: 1);
        stream.Events[0].Id = Guid.Empty;

        Planning.Plan(stream, StreamRowSnapshot.Live(1)).Apply();

        stream.Events[0].Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void a_second_pass_changes_nothing()
    {
        var stream = Planning.Action();

        // A transient retry re-enters the plan on actions the first attempt already stamped
        Planning.Plan(stream, StreamRowSnapshot.Live(4)).Apply();
        var ids = stream.Events.Select(x => x.Id).ToArray();

        var retried = Planning.Plan(stream, StreamRowSnapshot.Live(4),
            Planning.Context() with { Timestamp = Planning.Now.AddMinutes(1) });
        retried.Apply();

        stream.Events.Select(x => x.Version).ShouldBe([5, 6]);
        stream.Events.Select(x => x.Id).ShouldBe(ids);
        stream.Events.ShouldAllBe(x => x.Timestamp == Planning.Now);
        stream.Events[0].Headers!.Count.ShouldBe(1);
        stream.Version.ShouldBe(6);
    }

    [Fact]
    public void quick_modes_leave_the_sequence_for_the_database()
    {
        var stream = Planning.Action();

        Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(mode: EventAppendMode.Quick, sequences: [7, 8]))
            .Apply();

        stream.Events.ShouldAllBe(x => x.Sequence == 0);
    }

    [Fact]
    public void rich_mode_refuses_to_plan_a_write_it_has_no_sequence_for()
    {
        var stream = Planning.Action();

        // Rich promises an inline projection every event's sequence. Handing it a zero is a silent lie.
        Should.Throw<InvalidOperationException>(() => Planning.Plan(stream, StreamRowSnapshot.Live(1),
                Planning.Context(mode: EventAppendMode.Rich, sequences: [7])))
            .Message.ShouldContain("2 events with 1 reserved");

        stream.Events.ShouldAllBe(x => x.Version == 0);
    }

    [Fact]
    public void rich_mode_with_no_reservation_at_all_is_refused_too()
    {
        Should.Throw<InvalidOperationException>(() => Planning.Plan(Planning.Action(), StreamRowSnapshot.Live(1),
            Planning.Context(mode: EventAppendMode.Rich)));
    }

    [Fact]
    public void a_refused_rich_plan_reports_the_refusal_rather_than_the_sequences()
    {
        var plan = Planning.Plan(Planning.Action(StreamActionType.Start), StreamRowSnapshot.Live(1),
            Planning.Context(mode: EventAppendMode.Rich));

        Should.Throw<ExistingStreamIdCollisionException>(() => plan.Apply());
    }

    [Fact]
    public void a_refusal_beside_a_write_still_wins_over_a_short_reservation()
    {
        var writes = Planning.Action();
        var refused = Planning.Action(StreamActionType.Start);

        var plan = ClientSideAppendPlanner.Plan([writes, refused],
            x => x == refused ? StreamRowSnapshot.Live(1) : StreamRowSnapshot.Missing,
            Planning.Context(mode: EventAppendMode.Rich));

        Should.Throw<ExistingStreamIdCollisionException>(() => plan.Apply());
    }

    [Fact]
    public void a_reservation_is_measured_against_the_whole_plan()
    {
        // Enough for either stream alone, and not for the two of them
        Should.Throw<InvalidOperationException>(() => ClientSideAppendPlanner.Plan(
                [Planning.Action(), Planning.Action(events: 1)], _ => StreamRowSnapshot.Live(1),
                Planning.Context(mode: EventAppendMode.Rich, sequences: [10, 11])))
            .Message.ShouldContain("3 events with 2 reserved");
    }

    [Fact]
    public void rich_sequences_run_across_the_streams_in_plan_order()
    {
        var first = Planning.Action();
        var second = Planning.Action(events: 1);

        ClientSideAppendPlanner.Plan([first, second], _ => StreamRowSnapshot.Live(1),
            Planning.Context(mode: EventAppendMode.Rich, sequences: [10, 11, 12])).Apply();

        first.Events.Select(x => x.Sequence).ShouldBe([10, 11]);
        second.Events[0].Sequence.ShouldBe(12);
    }
}

/// <summary>
///     What happens to the expected version once the plan has used it, including the retry that re-enters
///     the plan, and the raised-event path where Polecat and Fisher disagreed.
/// </summary>
public class ClientSideAppendExpectedVersions
{
    [Fact]
    public void an_insert_keeps_the_callers_guard_for_a_retry()
    {
        var stream = Planning.Action();
        stream.ExpectedVersionOnServer = 0;

        Planning.Plan(stream, StreamRowSnapshot.Missing).Apply();
        stream.ExpectedVersionOnServer.ShouldBe(0);

        // Fisher cleared this. A transient failure re-enters the plan, the stream has appeared in the
        // meantime, and with the guard gone the append lands on a stream the caller said was new.
        var retried = Planning.Plan(stream, StreamRowSnapshot.Live(3));

        retried.Refusal.ShouldNotBeNull().Verdict.ShouldBe(StreamAppendVerdict.RefuseUnexpectedVersion);
    }

    [Fact]
    public void an_unguarded_update_takes_the_version_it_read_for_the_stream_row_write()
    {
        var stream = Planning.Action();

        Planning.Plan(stream, StreamRowSnapshot.Live(3)).Apply();

        stream.ExpectedVersionOnServer.ShouldBe(3);
    }

    [Fact]
    public void a_raised_event_is_numbered_from_the_read_and_not_from_the_slices_guess()
    {
        // The stream's real version is 9, and the projection only saw 5 of its events
        var stream = Planning.Action();
        stream.ExpectedVersionOnServer = 5;

        var context = Planning.Context(origin: AppendOrigin.RaisedByProjection);
        var plan = Planning.Plan(stream, StreamRowSnapshot.Live(9), context);

        plan.Steps[0].Verdict.ShouldBe(StreamAppendVerdict.UpdateStream);

        plan.Apply();

        stream.Events.Select(x => x.Version).ShouldBe([10, 11]);
        stream.ExpectedVersionOnServer.ShouldBe(9);
    }

    [Fact]
    public void a_raised_event_on_a_missing_stream_leaves_no_guess_behind()
    {
        var stream = Planning.Action();
        stream.ExpectedVersionOnServer = 5;

        Planning.Plan(stream, StreamRowSnapshot.Missing, Planning.Context(origin: AppendOrigin.RaisedByProjection))
            .Apply();

        stream.ExpectedVersionOnServer.ShouldBeNull();
        stream.Events.Select(x => x.Version).ShouldBe([1, 2]);
    }

    [Fact]
    public void a_raised_event_cannot_get_past_an_archive()
    {
        var plan = Planning.Plan(Planning.Action(), StreamRowSnapshot.Archived(3),
            Planning.Context(origin: AppendOrigin.RaisedByProjection));

        Should.Throw<ArchivedStreamException>(() => plan.Apply());
    }

    [Fact]
    public void the_same_action_in_a_unit_of_work_is_still_enforced()
    {
        var stream = Planning.Action();
        stream.ExpectedVersionOnServer = 5;

        Planning.Plan(stream, StreamRowSnapshot.Live(9)).Refusal.ShouldNotBeNull();
    }
}

/// <summary>
///     The split jasperfx#1047 found and left alone: <c>Append(stream, expectedVersion, ...)</c> is the
///     version after the append on Marten and Polecat, and before it on Fisher and the in-memory store.
///     These pin each store to its own reading through the one planner, so that a planner which quietly
///     picked one fails here, and so that unifying them later is a change someone has to make on purpose.
/// </summary>
public class ClientSideAppendExpectedVersionReadings
{
    private static RecordingOperations VersionAfterTheAppend() => new(StreamIdentity.AsGuid, "tenant-a");

    private static RecordingOperations VersionBeforeTheAppend()
        => new(StreamIdentity.AsGuid, "tenant-a") { ExpectedVersionIsTheStartingVersion = true };

    private static ClientSideAppendPlan Plan(RecordingOperations operations, long current, long expectedVersion,
        params object[] events)
    {
        var stream = operations.Append(Guid.NewGuid(), expectedVersion, events);
        return Planning.Plan(stream, StreamRowSnapshot.Live(current));
    }

    [Fact]
    public void version_after_reading_succeeds_one_short_of_the_argument()
    {
        Plan(VersionAfterTheAppend(), 1, 2, new QuestStarted("x")).Refusal.ShouldBeNull();
    }

    [Fact]
    public void version_after_reading_refuses_a_stream_already_at_the_argument()
    {
        Plan(VersionAfterTheAppend(), 2, 2, new QuestStarted("x")).Refusal.ShouldNotBeNull()
            .Verdict.ShouldBe(StreamAppendVerdict.RefuseUnexpectedVersion);
    }

    [Fact]
    public void version_before_reading_succeeds_at_the_argument()
    {
        Plan(VersionBeforeTheAppend(), 2, 2, new QuestStarted("x")).Refusal.ShouldBeNull();
    }

    [Fact]
    public void version_before_reading_refuses_a_stream_one_short_of_the_argument()
    {
        Plan(VersionBeforeTheAppend(), 1, 2, new QuestStarted("x")).Refusal.ShouldNotBeNull()
            .Verdict.ShouldBe(StreamAppendVerdict.RefuseUnexpectedVersion);
    }

    [Fact]
    public void several_events_move_the_argument_on_the_after_reading_and_not_on_the_before_reading()
    {
        object[] events = [new QuestStarted("x"), new MemberJoined("y"), new MemberJoined("z")];

        Plan(VersionAfterTheAppend(), 2, 5, events).Refusal.ShouldBeNull();
        Plan(VersionBeforeTheAppend(), 5, 5, events).Refusal.ShouldBeNull();
    }

    [Fact]
    public void version_before_reading_creates_a_stream_at_expected_version_zero()
    {
        var stream = VersionBeforeTheAppend().Append(Guid.NewGuid(), 0L, new QuestStarted("x"));

        // Fisher pins this one, and on the after reading the same call never reaches the planner
        Planning.Plan(stream, StreamRowSnapshot.Missing).Steps[0].Verdict.ShouldBe(StreamAppendVerdict.InsertStream);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            VersionAfterTheAppend().Append(Guid.NewGuid(), 0L, new QuestStarted("x")));
    }

    [Fact]
    public void the_planner_reads_the_starting_version_and_does_no_arithmetic_of_its_own()
    {
        var stream = Planning.Action(events: 3);
        stream.ExpectedVersionOnServer = 7;

        // Subtracting the pending events here would give the before reading the after reading's semantics
        Planning.Plan(stream, StreamRowSnapshot.Live(7)).Steps[0].Verdict.ShouldBe(StreamAppendVerdict.UpdateStream);
        Planning.Plan(stream, StreamRowSnapshot.Live(4)).Refusal.ShouldNotBeNull();
        Planning.Plan(stream, StreamRowSnapshot.Live(10)).Refusal.ShouldNotBeNull();
    }
}

/// <summary>
///     The shapes a unit of work can hand over, and above all the ones that lost events. The planner cannot
///     see Polecat's merged tenants or Fisher's replaced action, because both happen before it runs: what it
///     can do is never lose or renumber anything that does reach it.
/// </summary>
public class ClientSideAppendUnitsOfWork
{
    [Fact]
    public void two_actions_on_one_stream_are_folded_rather_than_numbered_twice()
    {
        var id = Guid.NewGuid();
        var first = Planning.Action(id: id);
        var second = Planning.Action(id: id, events: 3);

        var reads = 0;
        var plan = ClientSideAppendPlanner.Plan([first, second], _ =>
        {
            reads++;
            return StreamRowSnapshot.Live(4);
        }, Planning.Context());

        plan.Apply();

        reads.ShouldBe(1);
        first.Events.Select(x => x.Version).ShouldBe([5, 6]);
        second.Events.Select(x => x.Version).ShouldBe([7, 8, 9]);

        // Each stream row write has to find what the one before it left
        first.ExpectedVersionOnServer.ShouldBe(4);
        second.ExpectedVersionOnServer.ShouldBe(6);
        second.Version.ShouldBe(9);
    }

    [Fact]
    public void every_action_comes_back_so_no_event_can_go_missing()
    {
        var id = Guid.NewGuid();
        var actions = new[] { Planning.Action(id: id), Planning.Action(id: id), Planning.Action() };

        var plan = ClientSideAppendPlanner.Plan(actions, _ => StreamRowSnapshot.Missing, Planning.Context());

        plan.Writes.Select(x => x.Stream).ShouldBe(actions);
        plan.Writes.Sum(x => x.Stream.Events.Count).ShouldBe(6);
    }

    [Fact]
    public void the_first_action_on_a_new_stream_inserts_and_the_next_updates()
    {
        var id = Guid.NewGuid();

        var plan = ClientSideAppendPlanner.Plan([Planning.Action(id: id), Planning.Action(id: id)],
            _ => StreamRowSnapshot.Missing, Planning.Context());

        plan.Steps.Select(x => x.Verdict).ShouldBe([StreamAppendVerdict.InsertStream, StreamAppendVerdict.UpdateStream]);
    }

    [Fact]
    public void a_start_after_a_pending_action_is_a_collision_and_not_a_replacement()
    {
        var id = Guid.NewGuid();
        var pending = Planning.Action(id: id);
        var started = Planning.Action(StreamActionType.Start, id: id);

        // Fisher's dictionary drops the pending action at this point. Handed both, the plan refuses.
        var plan = ClientSideAppendPlanner.Plan([pending, started], _ => StreamRowSnapshot.Missing, Planning.Context());

        Should.Throw<ExistingStreamIdCollisionException>(() => plan.Apply());
        pending.Events.ShouldAllBe(x => x.Version == 0);
    }

    [Fact]
    public void a_second_start_on_one_id_is_a_collision_even_if_the_store_read_neither()
    {
        var id = Guid.NewGuid();

        var plan = ClientSideAppendPlanner.Plan(
            [Planning.Action(StreamActionType.Start, id: id), Planning.Action(StreamActionType.Start, id: id)],
            _ => StreamRowSnapshot.NotRead, Planning.Context());

        plan.Steps.Select(x => x.Verdict)
            .ShouldBe([StreamAppendVerdict.InsertStream, StreamAppendVerdict.RefuseCollision]);
    }

    [Fact]
    public void the_same_id_in_two_tenants_is_two_streams_under_conjoined_tenancy()
    {
        var id = Guid.NewGuid();
        var inA = Planning.Action(id: id, tenant: "tenant-a");
        var inB = Planning.Action(id: id, tenant: "tenant-b");

        var plan = ClientSideAppendPlanner.Plan([inA, inB],
            x => x.TenantId == "tenant-a" ? StreamRowSnapshot.Live(10) : StreamRowSnapshot.Missing,
            Planning.Context(tenancy: TenancyStyle.Conjoined));

        plan.Apply();

        plan.Steps.Select(x => x.Verdict).ShouldBe([StreamAppendVerdict.UpdateStream, StreamAppendVerdict.InsertStream]);
        inA.Events.Select(x => x.Version).ShouldBe([11, 12]);
        inB.Events.Select(x => x.Version).ShouldBe([1, 2]);
    }

    [Fact]
    public void the_same_id_in_two_tenants_is_one_stream_without_conjoined_tenancy()
    {
        var id = Guid.NewGuid();
        var inA = Planning.Action(id: id, tenant: "tenant-a");
        var inB = Planning.Action(id: id, tenant: "tenant-b");

        var plan = ClientSideAppendPlanner.Plan([inA, inB], _ => StreamRowSnapshot.Live(10),
            Planning.Context(tenancy: TenancyStyle.Single));

        plan.Apply();

        inB.Events.Select(x => x.Version).ShouldBe([13, 14]);
    }

    [Fact]
    public void string_keyed_streams_fold_on_their_key()
    {
        var first = new StreamAction("one", StreamActionType.Append) { TenantId = "tenant-a" }.AddEvent(Planning.NewEvent());
        var second = new StreamAction("one", StreamActionType.Append) { TenantId = "tenant-a" }.AddEvent(Planning.NewEvent());
        var other = new StreamAction("two", StreamActionType.Append) { TenantId = "tenant-a" }.AddEvent(Planning.NewEvent());

        ClientSideAppendPlanner.Plan([first, second, other], _ => StreamRowSnapshot.Live(1), Planning.Context()).Apply();

        second.Version.ShouldBe(3);
        other.Version.ShouldBe(2);
    }

    [Fact]
    public void a_version_check_is_against_the_row_and_not_the_units_own_append()
    {
        var id = Guid.NewGuid();
        var appended = Planning.Action(id: id);
        appended.ExpectedVersionOnServer = 4;

        var checkedOnly = Planning.Action(id: id, events: 0);
        checkedOnly.ExpectedVersionOnServer = 4;
        checkedOnly.AlwaysEnforceConsistency = true;

        // Polecat can hold both: FetchForWriting adds an action each time it is called
        var plan = ClientSideAppendPlanner.Plan([appended, checkedOnly], _ => StreamRowSnapshot.Live(4),
            Planning.Context());

        plan.Steps[1].Verdict.ShouldBe(StreamAppendVerdict.Verified);
    }

    [Fact]
    public void a_refusal_anywhere_leaves_every_action_untouched()
    {
        var fine = Planning.Action();
        var refused = Planning.Action(StreamActionType.Start);

        var plan = ClientSideAppendPlanner.Plan([fine, refused],
            x => x == refused ? StreamRowSnapshot.Live(1) : StreamRowSnapshot.Live(7), Planning.Context());

        Should.Throw<ExistingStreamIdCollisionException>(() => plan.Apply());

        // Fisher numbered as it went, so the first stream was left numbered when the second one threw
        fine.Events.ShouldAllBe(x => x.Version == 0);
        fine.Version.ShouldBe(0);
        fine.ExpectedVersionOnServer.ShouldBeNull();
    }

    [Fact]
    public void the_first_refusal_in_input_order_is_the_one_reported()
    {
        var archived = Planning.Action();
        var collided = Planning.Action(StreamActionType.Start);

        var plan = ClientSideAppendPlanner.Plan([archived, collided], _ => StreamRowSnapshot.Archived(2),
            Planning.Context());

        plan.Refusal!.Stream.ShouldBeSameAs(archived);
        Should.Throw<ArchivedStreamException>(() => plan.Apply());
    }

    [Fact]
    public void a_refused_stream_does_not_advance_the_fold()
    {
        var id = Guid.NewGuid();
        var refused = Planning.Action(id: id);
        refused.ExpectedVersionOnServer = 99;
        var next = Planning.Action(id: id);

        var plan = ClientSideAppendPlanner.Plan([refused, next], _ => StreamRowSnapshot.Live(4), Planning.Context());

        plan.Steps[1].CurrentVersion.ShouldBe(4);
    }

    [Fact]
    public void an_event_of_another_tenant_is_refused_and_not_moved()
    {
        var stream = Planning.Action();
        stream.Events[1].TenantId = "tenant-b";

        var plan = Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(tenancy: TenancyStyle.Conjoined));

        Should.Throw<InvalidOperationException>(() => plan.Apply()).Message.ShouldContain("tenant-a");

        // Polecat overwrote this, moving the event into the stream's tenant without a word. Fisher wrote it,
        // and a row under another tenant than its stream row is an event its own stream cannot find.
        stream.Events[1].TenantId.ShouldBe("tenant-b");
    }

    [Fact]
    public void an_event_tenant_means_nothing_without_conjoined_tenancy()
    {
        var stream = Planning.Action();
        stream.Events[1].TenantId = "tenant-b";

        ClientSideAppendPlanner.WorkFor(stream, TenancyStyle.Single).ShouldBe(StreamAppendWork.Append);

        // Left as it is, because only an event with no tenant is the plan's to fill in
        Planning.Plan(stream, StreamRowSnapshot.Live(1), Planning.Context(tenancy: TenancyStyle.Single)).Apply();

        stream.Events[1].TenantId.ShouldBe("tenant-b");
    }

    /// <summary>
    ///     What the planner cannot do, pinned so that nobody takes the tests above for more than they are.
    ///     <see cref="RecordingOperations" /> finds a pending action by id alone, as Polecat's and Marten's
    ///     <c>TryFindStream</c> do, and <c>StreamAction.AddEvent</c> restamps the event, so the second tenant
    ///     is gone before a plan exists. The fix belongs in that lookup.
    /// </summary>
    [Fact]
    public void a_merge_that_happened_before_the_plan_is_invisible_to_it()
    {
        var operations = new RecordingOperations(StreamIdentity.AsGuid, "tenant-a");
        var id = Guid.NewGuid();

        operations.ForTenant("tenant-a").Append(id, new QuestStarted("in tenant a"));
        var merged = operations.ForTenant("tenant-b").Append(id, new MemberJoined("in tenant b"));

        merged.Events.Count.ShouldBe(2);
        merged.Events.ShouldAllBe(x => x.TenantId == "tenant-a");

        ClientSideAppendPlanner.WorkFor(merged, TenancyStyle.Conjoined).ShouldBe(StreamAppendWork.Append);
    }

    [Fact]
    public void an_action_nobody_stamped_a_tenant_on_is_not_a_mixed_one()
    {
        var stream = new StreamAction(Guid.NewGuid(), StreamActionType.Append).AddEvent(Planning.NewEvent());
        stream.Events[0].TenantId = "tenant-a";

        ClientSideAppendPlanner.WorkFor(stream, TenancyStyle.Conjoined).ShouldBe(StreamAppendWork.Append);
    }

    [Fact]
    public void a_stream_with_nothing_to_do_is_not_read()
    {
        var plan = ClientSideAppendPlanner.Plan([Planning.Action(events: 0)],
            _ => throw new InvalidOperationException("Not a stream to read"), Planning.Context());

        plan.Steps[0].Verdict.ShouldBe(StreamAppendVerdict.Skip);
    }

    [Fact]
    public void a_store_keeps_its_own_exceptions()
    {
        var refusals = new StoreRefusals();
        var context = Planning.Context(refusals: refusals);

        Should.Throw<StoreCollision>(() =>
            Planning.Plan(Planning.Action(StreamActionType.Start), StreamRowSnapshot.Live(1), context).Apply());

        Should.Throw<StoreArchived>(() =>
            Planning.Plan(Planning.Action(), StreamRowSnapshot.Archived(1), context).Apply());

        var stale = Planning.Action();
        stale.ExpectedVersionOnServer = 3;
        Should.Throw<StoreConflict>(() => Planning.Plan(stale, StreamRowSnapshot.Live(1), context).Apply());
    }

    private sealed class StoreCollision(object id) : ExistingStreamIdCollisionException(id);

    private sealed class StoreArchived(object id) : ArchivedStreamException(id);

    private sealed class StoreConflict() : EventStreamUnexpectedMaxEventIdException("conflict");

    private sealed class StoreRefusals : AppendRefusals
    {
        public override Exception Collision(object id, Type? aggregateType) => new StoreCollision(id);

        public override Exception Archived(object id) => new StoreArchived(id);

        public override Exception UnexpectedVersion(object id, Type? aggregateType, long expected, long actual)
            => new StoreConflict();
    }
}

/// <summary>
///     Fisher's pass ahead of its inline projections, which runs outside the write lock and so must not
///     decide anything.
/// </summary>
public class ClientSideAppendNumberingAhead
{
    [Fact]
    public void numbers_and_stamps_so_a_projection_has_something_to_read()
    {
        var stream = Planning.Action();

        ClientSideAppendPlanner.NumberAhead([stream], _ => StreamRowSnapshot.Live(3), Planning.Context());

        stream.Events.Select(x => x.Version).ShouldBe([4, 5]);
        stream.Version.ShouldBe(5);
        stream.Events.ShouldAllBe(x => x.Timestamp == Planning.Now && x.CorrelationId == "session-correlation");
    }

    [Fact]
    public void leaves_the_expected_version_for_the_real_check()
    {
        var stream = Planning.Action();
        stream.ExpectedVersionOnServer = 2;

        ClientSideAppendPlanner.NumberAhead([stream], _ => StreamRowSnapshot.Live(3), Planning.Context());

        // Setting this here would make the check under the lock compare a value with itself
        stream.ExpectedVersionOnServer.ShouldBe(2);
        Planning.Plan(stream, StreamRowSnapshot.Live(3)).Refusal.ShouldNotBeNull();
    }

    [Fact]
    public void refuses_nothing_because_it_knows_nothing()
    {
        var archived = Planning.Action();
        var collided = Planning.Action(StreamActionType.Start);
        var stale = Planning.Action();
        stale.ExpectedVersionOnServer = 40;

        Should.NotThrow(() => ClientSideAppendPlanner.NumberAhead([archived, collided, stale],
            x => x == archived ? StreamRowSnapshot.Archived(1) : StreamRowSnapshot.Live(1), Planning.Context()));

        archived.Version.ShouldBe(3);
    }

    [Fact]
    public void the_real_plan_renumbers_from_its_own_read()
    {
        var stream = Planning.Action();

        ClientSideAppendPlanner.NumberAhead([stream], _ => StreamRowSnapshot.Live(3), Planning.Context());

        // A racing writer got in before the lock was taken
        Planning.Plan(stream, StreamRowSnapshot.Live(6)).Apply();

        stream.Events.Select(x => x.Version).ShouldBe([7, 8]);
        stream.Version.ShouldBe(8);
    }

    [Fact]
    public void folds_two_actions_on_one_stream()
    {
        var id = Guid.NewGuid();
        var first = Planning.Action(id: id);
        var second = Planning.Action(id: id);

        ClientSideAppendPlanner.NumberAhead([first, second], _ => StreamRowSnapshot.Live(3), Planning.Context());

        second.Events.Select(x => x.Version).ShouldBe([6, 7]);
    }

    [Fact]
    public void keeps_tenants_apart()
    {
        var id = Guid.NewGuid();
        var inA = Planning.Action(id: id, tenant: "tenant-a");
        var inB = Planning.Action(id: id, tenant: "tenant-b");

        ClientSideAppendPlanner.NumberAhead([inA, inB],
            x => x.TenantId == "tenant-a" ? StreamRowSnapshot.Live(3) : StreamRowSnapshot.Missing, Planning.Context());

        inB.Events.Select(x => x.Version).ShouldBe([1, 2]);
    }

    [Fact]
    public void the_same_id_is_one_stream_without_conjoined_tenancy()
    {
        var id = Guid.NewGuid();
        var inA = Planning.Action(id: id, tenant: "tenant-a");
        var inB = Planning.Action(id: id, tenant: "tenant-b");

        ClientSideAppendPlanner.NumberAhead([inA, inB], _ => StreamRowSnapshot.Live(3),
            Planning.Context(tenancy: TenancyStyle.Single));

        inB.Events.Select(x => x.Version).ShouldBe([6, 7]);
    }

    [Fact]
    public void skips_a_stream_with_no_events_without_reading_it()
    {
        Should.NotThrow(() => ClientSideAppendPlanner.NumberAhead([Planning.Action(events: 0)],
            _ => throw new InvalidOperationException("Not a stream to read"), Planning.Context()));
    }

    [Fact]
    public void an_unread_row_numbers_from_the_start_because_it_is_only_a_preview()
    {
        var stream = Planning.Action(StreamActionType.Start);

        ClientSideAppendPlanner.NumberAhead([stream], _ => StreamRowSnapshot.NotRead, Planning.Context());

        stream.Events.Select(x => x.Version).ShouldBe([1, 2]);
    }
}
