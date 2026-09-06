using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using JasperFx.Events.TestSupport;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Projection scenario events, aggregates and observer

public record ScenarioQuestStarted(string Name);

public record ScenarioMemberJoined(string Member);

public record ScenarioQuestEnded;

/// <summary>
/// Guid-keyed snapshot the scenario asserts against.
/// </summary>
public partial class ScenarioQuest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public bool Ended { get; set; }

    public static ScenarioQuest Create(ScenarioQuestStarted e) => new() { Name = e.Name };

    public void Apply(ScenarioMemberJoined e) => Members.Add(e.Member);

    public void Apply(ScenarioQuestEnded _) => Ended = true;
}

/// <summary>
/// The string-keyed twin. Its whole job is to make the scenario's <c>LoadDocumentAsync(session,
/// object id, ...)</c> dispatch on a <see cref="string"/> rather than a <see cref="Guid"/>.
/// </summary>
public partial class ScenarioQuestByKey
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();

    public static ScenarioQuestByKey Create(ScenarioQuestStarted e) => new() { Name = e.Name };

    public void Apply(ScenarioMemberJoined e) => Members.Add(e.Member);
}

/// <summary>
/// A plain document no projection produces, seeded by hand. The scenario's teardown must leave it
/// alone.
/// </summary>
public class ScenarioBystander
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Records what the harness reported, so the observer contract can be asserted rather than assumed.
/// </summary>
public class RecordingScenarioObserver: IProjectionScenarioObserver
{
    public List<ProjectionScenarioStepDescription> Started { get; } = new();
    public List<ProjectionScenarioStepDescription> Succeeded { get; } = new();
    public List<ProjectionScenarioStepDescription> Failed { get; } = new();
    public List<ProjectionScenarioStepDescription> Skipped { get; } = new();

    public void StepStarted(ProjectionScenarioStepDescription step) => Started.Add(step);

    public void StepSucceeded(ProjectionScenarioStepDescription step) => Succeeded.Add(step);

    public void StepFailed(ProjectionScenarioStepDescription step, Exception exception) => Failed.Add(step);

    public void StepSkipped(ProjectionScenarioStepDescription step) => Skipped.Add(step);
}

#endregion

/// <summary>
/// The <see cref="ProjectionScenario{TOperations,TQuerySession}"/> test harness — scripted appends
/// and assertions, executed against a real store.
/// </summary>
/// <remarks>
/// <para>
/// An unusual suite, because the harness is not merely shared <em>behaviour</em>: it is shared
/// <em>code</em>, living in <c>JasperFx.Events/TestSupport</c>, which every product subclasses over
/// a five-member seam. So the failures worth catching are not in the sequencing logic — one copy of
/// that exists — but underneath it, in what a store supplies through the seam:
/// <c>DeleteExistingDataAsync</c>, <c>HasAnyAsyncProjections</c>, <c>BuildDaemonAsync</c>,
/// <c>OpenSession</c>, <c>LoadDocumentAsync</c>. A store can subclass the harness perfectly, wire one
/// of those five wrongly, and leave every other suite in this library green. That is what each fact
/// below is aimed at, and why the assertions are about observable store state rather than about the
/// harness's internal bookkeeping.
/// </para>
/// <para>
/// The store's own entry point is deliberately part of the surface under test, not an implementation
/// detail the fixture may route around — see
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.RunProjectionScenarioAsync"/>.
/// All three products spell it as three lines (construct, configure, execute), and a fixture that
/// inlined those three lines would pass this whole suite while the store's advertised
/// <c>Advanced.EventProjectionScenario</c> was missing or pointed at a different store.
/// </para>
/// <para>
/// Deliberately out of scope: the composite-projection variant of the clean-slate fact
/// (marten#5169, where the wipe list came from <c>StorageTypes</c> rather than
/// <c>PublishedTypes()</c>, so a composite read side wiped nothing and every scenario after the
/// first ran against the previous one's documents while its events were gone). Composites are opt-in
/// through a different seam entirely, and requiring one here would force every store enrolling this
/// suite to support composite projections as well. The portable half —
/// <see cref="a_second_scenario_starts_from_a_clean_slate"/> — is asserted; the composite-specific
/// half stays in the product that has the bug's shape.
/// </para>
/// <para>
/// Also out of scope: multi-tenanted scenarios (<c>TenantId</c>). The property is on the shared
/// harness, but a tenanted scenario needs a conjoined-tenancy store, and pairing that gate with this
/// one would make the most valuable facts here skip on any store that has one but not the other.
/// </para>
/// </remarks>
public abstract class ProjectionScenarioCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_scenario";
        config.AddEventType<ScenarioQuestEnded>();
        config.Snapshot<ScenarioQuest>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = "compliance_scenario_async";
        config.AddEventType<ScenarioQuestEnded>();
        config.Snapshot<ScenarioQuest>(SnapshotLifecycle.Async);
    };

    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_scenario_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.Snapshot<ScenarioQuestByKey>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void assertSupported() => Assert.SkipUnless(theFixture.SupportsProjectionScenario,
        "This event store does not implement the shared ProjectionScenario harness");

    private Task runAsync(Action<ProjectionScenario<TOperations, TQuerySession>> configure)
        => theFixture.RunProjectionScenarioAsync(configure, Cancellation);

    // ---------- The happy path ----------

    [Fact]
    public async Task a_scenario_asserts_against_the_projected_document()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId,
                new ScenarioQuestStarted("Destroy the Ring"),
                new ScenarioMemberJoined("Frodo"));

            scenario.DocumentShouldExist<ScenarioQuest>(streamId, quest =>
            {
                quest.Name.ShouldBe("Destroy the Ring");
                quest.Members.ShouldBe(new[] { "Frodo" });
            });
        });
    }

    /// <summary>
    /// Consecutive appends batch into one commit and the pending work is flushed whenever the next
    /// step is an assertion — so an assertion in the middle of a script must see everything queued
    /// before it and nothing queued after it.
    /// </summary>
    [Fact]
    public async Task steps_run_in_order_with_assertions_seeing_only_what_preceded_them()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));
            scenario.Append(streamId, new ScenarioMemberJoined("Frodo"));

            scenario.DocumentShouldExist<ScenarioQuest>(streamId,
                quest => quest.Members.ShouldBe(new[] { "Frodo" }));

            scenario.Append(streamId, new ScenarioMemberJoined("Sam"), new ScenarioQuestEnded());

            scenario.DocumentShouldExist<ScenarioQuest>(streamId, quest =>
            {
                quest.Members.ShouldBe(new[] { "Frodo", "Sam" });
                quest.Ended.ShouldBeTrue();
            });
        });
    }

    [Fact]
    public async Task document_should_not_exist_passes_when_nothing_was_projected()
    {
        assertSupported();

        await runAsync(scenario =>
        {
            scenario.Append(Guid.NewGuid(), new ScenarioMemberJoined("Nobody"));
            scenario.DocumentShouldNotExist<ScenarioQuest>(Guid.NewGuid());
        });
    }

    /// <summary>
    /// The general escape hatch: an arbitrary assertion against a query session the harness supplies,
    /// rather than one of the two document shorthands.
    /// </summary>
    [Fact]
    public async Task the_general_assertion_hook_gets_a_usable_query_session()
    {
        assertSupported();

        var streamId = Guid.NewGuid();
        var ran = false;

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));

            scenario.AssertAgainstProjectedData("the quest was projected", async (session, ct) =>
            {
                var quest = await theFixture.LoadDocumentAsync<ScenarioQuest>(session, streamId, ct);
                quest.ShouldNotBeNull();
                quest.Name.ShouldBe("Destroy the Ring");
                ran = true;
            });
        });

        // A hook that never ran would leave this fact vacuously green.
        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task the_append_events_lambda_runs_arbitrary_event_operations()
    {
        assertSupported();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await runAsync(scenario =>
        {
            scenario.AppendEvents("start two quests", events =>
            {
                events.StartStream<ScenarioQuest>(first, new ScenarioQuestStarted("First"));
                events.StartStream<ScenarioQuest>(second, new ScenarioQuestStarted("Second"));
            });

            scenario.DocumentShouldExist<ScenarioQuest>(first, q => q.Name.ShouldBe("First"));
            scenario.DocumentShouldExist<ScenarioQuest>(second, q => q.Name.ShouldBe("Second"));
        });
    }

    [Fact]
    public async Task append_accepts_an_enumerable_of_events()
    {
        assertSupported();

        var streamId = Guid.NewGuid();
        IEnumerable<object> members =
            new object[] { new ScenarioMemberJoined("Frodo"), new ScenarioMemberJoined("Sam") };

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));
            scenario.Append(streamId, members);

            scenario.DocumentShouldExist<ScenarioQuest>(streamId,
                quest => quest.Members.Count.ShouldBe(2));
        });
    }

    [Fact]
    public async Task start_stream_hands_back_the_generated_stream_id()
    {
        assertSupported();

        var streamId = Guid.Empty;

        await runAsync(scenario =>
        {
            streamId = scenario.StartStream<ScenarioQuest>(new ScenarioQuestStarted("Destroy the Ring"));
            scenario.DocumentShouldExist<ScenarioQuest>(streamId);
        });

        streamId.ShouldNotBe(Guid.Empty);

        // The id the script was handed is the id the events actually landed under.
        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(1);
        events.Single().Data.ShouldBeOfType<ScenarioQuestStarted>().Name.ShouldBe("Destroy the Ring");
    }

    // ---------- The sad path ----------

    /// <summary>
    /// The failure has to be distinguishable: a failed <em>assertion</em> is the scenario reporting
    /// on the store, and a failure inside an append is the store breaking. Both arrive inside a
    /// <see cref="ProjectionScenarioException"/>, and only the inner type tells them apart.
    /// </summary>
    [Fact]
    public async Task a_failed_assertion_arrives_as_a_typed_inner_exception()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(async () =>
        {
            await runAsync(scenario =>
            {
                scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));

                // Fails: the document DOES exist by now.
                scenario.DocumentShouldNotExist<ScenarioQuest>(streamId);
            });
        });

        ex.InnerExceptions.Single().ShouldBeOfType<ProjectionScenarioAssertionException>();
    }

    /// <summary>
    /// A failed action means every later step would run against a state nobody intended, so the run
    /// stops. Failed assertions accumulate instead — the state is still the intended one.
    /// </summary>
    [Fact]
    public async Task a_failed_action_stops_the_run_and_skips_the_remaining_steps()
    {
        assertSupported();

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(async () =>
        {
            await runAsync(scenario =>
            {
                scenario.AppendEvents("an action that blows up",
                    _ => throw new DivideByZeroException("boom"));

                // Neither should run. The second would fail loudly if it did.
                scenario.DocumentShouldNotExist<ScenarioQuest>(Guid.NewGuid());
                scenario.DocumentShouldExist<ScenarioQuest>(Guid.NewGuid());
            });
        });

        ex.InnerExceptions.Single().ShouldBeOfType<DivideByZeroException>();
        ex.Message.ShouldContain("Skipped the remaining 2 step(s)");
    }

    [Fact]
    public async Task every_failed_assertion_is_reported_not_just_the_first()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        var ex = await Should.ThrowAsync<ProjectionScenarioException>(async () =>
        {
            await runAsync(scenario =>
            {
                scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));

                scenario.DocumentShouldNotExist<ScenarioQuest>(streamId);
                scenario.DocumentShouldExist<ScenarioQuest>(Guid.NewGuid());
            });
        });

        ex.InnerExceptions.Count.ShouldBe(2);
    }

    /// <summary>
    /// The steps are consumed by the first run, so a second run would otherwise be a silent no-op
    /// that passes — the worst possible outcome for a test harness.
    /// </summary>
    [Fact]
    public async Task a_scenario_cannot_be_executed_twice()
    {
        assertSupported();

        var scenario = theFixture.CreateProjectionScenario();
        scenario.StartStream<ScenarioQuest>(Guid.NewGuid(), new ScenarioQuestStarted("Once"));

        await scenario.ExecuteAsync(Cancellation);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await scenario.ExecuteAsync(Cancellation);
        });
    }

    // ---------- Commit boundaries ----------

    /// <summary>
    /// An action only flushes when the step after it is an assertion, so whatever a trailing action
    /// queued is still in the session when the run ends — and the run disposes that session.
    /// marten#5126: an append with no assertion after it is still an append.
    /// </summary>
    [Fact]
    public async Task a_trailing_append_with_no_assertion_after_it_is_still_committed()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));
            scenario.DocumentShouldExist<ScenarioQuest>(streamId);

            // Nothing follows this.
            scenario.Append(streamId, new ScenarioMemberJoined("Frodo"));
        });

        await using var session = OpenSession();
        var quest = await LoadDocumentAsync<ScenarioQuest>(session, streamId);
        quest.ShouldNotBeNull();
        quest.Members.ShouldBe(new[] { "Frodo" });
    }

    /// <summary>
    /// The degenerate case of the same rule: a scenario that only arranges must not be a silent
    /// no-op that passes.
    /// </summary>
    [Fact]
    public async Task an_arrange_only_scenario_actually_writes()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
            scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring")));

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(1);
    }

    // ---------- Teardown ----------

    [Fact]
    public async Task a_second_scenario_starts_from_a_clean_slate()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
            scenario.StartStream<ScenarioQuest>(streamId,
                new ScenarioQuestStarted("Destroy the Ring"), new ScenarioMemberJoined("Frodo")));

        // Same stream id, same events. A store whose teardown wiped the events but left the
        // projected documents behind reads back exactly doubled here, which a last-write-wins
        // aggregate would hide — hence the additive Members list.
        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId,
                new ScenarioQuestStarted("Destroy the Ring"), new ScenarioMemberJoined("Frodo"));

            scenario.DocumentShouldExist<ScenarioQuest>(streamId,
                quest => quest.Members.ShouldBe(new[] { "Frodo" }));
        });

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(2);
    }

    /// <summary>
    /// The teardown is scoped to event data and the storage of registered projections. A scenario is
    /// entitled to seed documents its projections do not produce, and clearing those would make the
    /// harness quietly destructive.
    /// </summary>
    [Fact]
    public async Task the_teardown_leaves_unrelated_documents_alone()
    {
        assertSupported();

        var seeded = Guid.NewGuid();

        await using (var writer = OpenSession())
        {
            StoreDocument(writer, new ScenarioBystander { Id = seeded, Note = "keep me" });
            await SaveChangesAsync(writer);
        }

        await runAsync(scenario =>
            scenario.StartStream<ScenarioQuest>(Guid.NewGuid(), new ScenarioQuestStarted("Destroy the Ring")));

        await using var session = OpenSession();
        var bystander = await LoadDocumentAsync<ScenarioBystander>(session, seeded);
        bystander.ShouldNotBeNull();
        bystander.Note.ShouldBe("keep me");
    }

    [Fact]
    public async Task opting_out_of_the_teardown_retains_prior_events_and_documents()
    {
        assertSupported();

        var streamId = Guid.NewGuid();

        await using (var writer = OpenSession())
        {
            EventsFor(writer).StartStream<ScenarioQuest>(streamId,
                new ScenarioQuestStarted("Existing"), new ScenarioMemberJoined("Frodo"));
            await SaveChangesAsync(writer);
        }

        await runAsync(scenario =>
        {
            scenario.DeleteExistingData = false;

            // Still here, because the scenario did not wipe the store.
            scenario.DocumentShouldExist<ScenarioQuest>(streamId, quest =>
            {
                quest.Name.ShouldBe("Existing");
                quest.Members.ShouldBe(new[] { "Frodo" });
            });
        });
    }

    // ---------- The plan and the observer (jasperfx#688) ----------

    /// <summary>
    /// A runner renders the plan before the run and the outcomes during it, so both have to be
    /// reachable and their numbering has to agree.
    /// </summary>
    [Fact]
    public async Task the_plan_is_readable_before_the_run_and_the_observer_sees_every_step()
    {
        assertSupported();

        var streamId = Guid.NewGuid();
        var observer = new RecordingScenarioObserver();

        // Built by hand rather than through the run entry point, so the plan can be read on both
        // sides of the run — "unchanged by execution" is half the claim, and the steps are consumed
        // by the run.
        var scenario = theFixture.CreateProjectionScenario();
        scenario.Observer = observer;
        scenario.StartStream<ScenarioQuest>(streamId, new ScenarioQuestStarted("Destroy the Ring"));
        scenario.DocumentShouldExist<ScenarioQuest>(streamId);

        var planned = scenario.PlannedSteps;
        planned.Count.ShouldBe(2);
        planned[0].Number.ShouldBe(1);
        planned[0].Kind.ShouldBe(ProjectionScenarioStepKind.Action);
        planned[1].Number.ShouldBe(2);
        planned[1].Kind.ShouldBe(ProjectionScenarioStepKind.Assertion);

        await scenario.ExecuteAsync(Cancellation);

        scenario.PlannedSteps.Count.ShouldBe(2);
        scenario.PlannedSteps.Select(x => x.Number).ShouldBe(new[] { 1, 2 });

        observer.Started.Select(x => x.Number).ShouldBe(new[] { 1, 2 });
        observer.Succeeded.Select(x => x.Number).ShouldBe(new[] { 1, 2 });
        observer.Failed.ShouldBeEmpty();
        observer.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_observer_is_told_which_steps_were_skipped()
    {
        assertSupported();

        var observer = new RecordingScenarioObserver();

        await Should.ThrowAsync<ProjectionScenarioException>(async () =>
        {
            await runAsync(scenario =>
            {
                scenario.Observer = observer;

                scenario.AppendEvents("an action that blows up",
                    _ => throw new DivideByZeroException("boom"));
                scenario.DocumentShouldExist<ScenarioQuest>(Guid.NewGuid());
            });
        });

        observer.Failed.Select(x => x.Number).ShouldBe(new[] { 1 });
        observer.Skipped.Select(x => x.Number).ShouldBe(new[] { 2 });
        observer.Succeeded.ShouldBeEmpty();
    }

    // ---------- String stream identity ----------

    /// <summary>
    /// The harness loads documents through <c>LoadDocumentAsync(session, object id, ...)</c>, which
    /// every store implements as a dispatch on the id's runtime type. A store that only wired the
    /// <see cref="Guid"/> arm passes every other fact here.
    /// </summary>
    [Fact]
    public async Task a_string_key_flows_through_the_object_id_load_dispatch()
    {
        assertSupported();

        await theFixture.ConfigureAsync(_stringConfiguration);
        await theFixture.CleanEventDataAsync();

        var key = $"quest/{Guid.NewGuid():N}";

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuestByKey>(key,
                new ScenarioQuestStarted("Destroy the Ring"), new ScenarioMemberJoined("Frodo"));

            scenario.DocumentShouldExist<ScenarioQuestByKey>(key, quest =>
            {
                quest.Name.ShouldBe("Destroy the Ring");
                quest.Members.ShouldBe(new[] { "Frodo" });
            });

            scenario.DocumentShouldNotExist<ScenarioQuestByKey>($"quest/{Guid.NewGuid():N}");
        });
    }

    // ---------- The async daemon ----------

    /// <summary>
    /// With an async projection registered the harness stands up a daemon for the run and waits for
    /// non-stale data before every assertion. Nothing else in this suite exercises
    /// <c>HasAnyAsyncProjections</c> or <c>BuildDaemonAsync</c>, and a store that returned false from
    /// the first would pass every inline fact while every async assertion raced.
    /// </summary>
    [Fact]
    public async Task an_async_projection_gets_a_daemon_for_the_duration_of_the_run()
    {
        assertSupported();

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        await runAsync(scenario =>
        {
            scenario.StartStream<ScenarioQuest>(streamId,
                new ScenarioQuestStarted("Destroy the Ring"), new ScenarioMemberJoined("Frodo"));

            // Only reachable if the daemon ran and the scenario waited for it.
            scenario.DocumentShouldExist<ScenarioQuest>(streamId,
                quest => quest.Members.ShouldBe(new[] { "Frodo" }));

            scenario.Append(streamId, new ScenarioMemberJoined("Sam"));

            scenario.DocumentShouldExist<ScenarioQuest>(streamId,
                quest => quest.Members.ShouldBe(new[] { "Frodo", "Sam" }));
        });
    }
}
