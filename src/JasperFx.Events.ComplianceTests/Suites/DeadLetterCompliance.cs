using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Dead letter events and aggregate

public record ReactorStarted(string Name);

public record ReactorPulsed(int Output);

/// <summary>
/// Applying this event always throws. It is the whole mechanism of this suite: a poison event in an
/// otherwise ordinary stream, so the error path can be exercised without any store-specific hook.
/// </summary>
public record ReactorFaulted(string Reason);

public partial class ComplianceReactor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Output { get; set; }

    public static ComplianceReactor Create(ReactorStarted e) => new() { Name = e.Name };

    public void Apply(ReactorPulsed e) => Output += e.Output;

    public void Apply(ReactorFaulted e)
        => throw new InvalidOperationException($"Reactor fault: {e.Reason}");
}

#endregion

/// <summary>
/// The projection error path — what happens to an async projection when applying an event throws,
/// and what the store records about it.
/// </summary>
/// <remarks>
/// <para>
/// Two behaviours are worth pinning across stores and neither is obvious. First, that a skip policy
/// actually <em>skips</em>: the shard survives the poison event and keeps projecting the events
/// after it, rather than stopping or silently dropping the rest of the stream. Second, that the
/// skipped event is <em>recorded</em> rather than lost — a dead letter row a human or a monitoring
/// tool can find, carrying enough to identify the event and the failure.
/// </para>
/// <para>
/// The error policy is reached through <c>IEventStore&lt;TOperations, TQuerySession&gt;.ContinuousErrors</c>,
/// and the dead letters through <c>IEventStore.AllDatabases()</c> →
/// <c>IEventDatabase.QueryDeadLetterEventsAsync</c>. Both are already shared, so this suite needs no
/// seam addition — the reason it moved out of the "needs a seam" group (marten#5149). The cast from
/// the fixture's non-generic <see cref="IEventStore"/> to the closed generic is safe because this
/// suite is generic over the same pair the store closes over.
/// </para>
/// <para>
/// Deliberately not asserted: the stop-on-error policy. Whether a shard pauses, stops, or faults —
/// and how a caller observes that — is expressed differently enough between the products that
/// encoding one shape here would pin an implementation rather than a promise. The skip path is the
/// one both products document.
/// </para>
/// </remarks>
public abstract class DeadLetterCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_dead_letters";
        config.Snapshot<ComplianceReactor>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");
    }

    /// <summary>
    /// Turn on skip-and-record for apply errors, through the shared error-handling options.
    /// </summary>
    private void skipApplyErrors()
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;
        store.ContinuousErrors.SkipApplyErrors = true;
    }

    private async Task<Guid> aFaultingReactorAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceReactor>(streamId,
            new ReactorStarted("Alpha"),
            new ReactorPulsed(10),
            new ReactorFaulted("coolant"),
            new ReactorPulsed(5));
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<DeadLetterEvent[]> deadLettersAsync()
    {
        var databases = await theFixture.EventStore.AllDatabases();

        // Compose rather than a constructor: jasperfx#419 made it the single path for building a
        // shard identity, and the products only agree on the grammar it produces.
        var shard = ShardName.Compose(nameof(ComplianceReactor));

        var all = new System.Collections.Generic.List<DeadLetterEvent>();
        foreach (var database in databases)
        {
            var rows = await database
                .QueryDeadLetterEventsAsync(shard, null, 0, 100, Cancellation);
            all.AddRange(rows);
        }

        return all.ToArray();
    }

    [Fact]
    public async Task the_shard_survives_a_poison_event_and_keeps_going()
    {
        SkipUnlessDaemonIsSupported();

        skipApplyErrors();
        var streamId = await aFaultingReactorAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();
        var reactor = await LoadDocumentAsync<ComplianceReactor>(query, streamId);

        reactor.ShouldNotBeNull();
        reactor.Name.ShouldBe("Alpha");

        // 10 before the fault and 5 after it: the poison event was skipped, not the rest of the
        // stream. A store that stopped at the fault would report 10.
        reactor.Output.ShouldBe(15);
    }

    [Fact]
    public async Task the_skipped_event_is_recorded_as_a_dead_letter()
    {
        SkipUnlessDaemonIsSupported();

        skipApplyErrors();
        await aFaultingReactorAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var deadLetters = await deadLettersAsync();

        deadLetters.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task the_dead_letter_row_identifies_the_projection_and_the_failure()
    {
        SkipUnlessDaemonIsSupported();

        skipApplyErrors();
        await aFaultingReactorAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        var deadLetter = (await deadLettersAsync()).First();

        deadLetter.ProjectionName.ShouldBe(nameof(ComplianceReactor));

        // Enough to find the offending event again, and enough to say what went wrong.
        deadLetter.EventSequence.ShouldBeGreaterThan(0);
        deadLetter.ExceptionMessage.ShouldNotBeNullOrEmpty();

        // ExceptionType names the INNER exception -- the one the projection actually threw -- while
        // ExceptionMessage carries the wrapping ApplyEventException's own message, which is composed
        // by the daemon and is not the place to look for the original reason. Asserting the inner
        // type rather than searching the message is what the shared ctor actually promises
        // (DeadLetterEvent.cs: ExceptionType = ex.InnerException?.GetType().NameInCode()).
        deadLetter.ExceptionType.ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task a_stream_with_no_faults_produces_no_dead_letters()
    {
        SkipUnlessDaemonIsSupported();

        skipApplyErrors();

        var streamId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceReactor>(streamId,
                new ReactorStarted("Beta"), new ReactorPulsed(7));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        (await deadLettersAsync()).ShouldBeEmpty();

        await using var query = OpenSession();
        var reactor = await LoadDocumentAsync<ComplianceReactor>(query, streamId);
        reactor.ShouldNotBeNull();
        reactor.Output.ShouldBe(7);
    }

    [Fact]
    public async Task one_faulting_stream_does_not_stop_another_stream_from_projecting()
    {
        SkipUnlessDaemonIsSupported();

        skipApplyErrors();

        await aFaultingReactorAsync();

        var healthyId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceReactor>(healthyId,
                new ReactorStarted("Gamma"), new ReactorPulsed(42));
            await SaveChangesAsync(session);
        }

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();
        var healthy = await LoadDocumentAsync<ComplianceReactor>(query, healthyId);

        healthy.ShouldNotBeNull();
        healthy.Output.ShouldBe(42);
    }

    [Fact]
    public async Task skip_apply_errors_is_readable_back_off_the_shared_options()
    {
        // Not a daemon test -- it pins that the error-handling options a caller sets are the same
        // live object the store consults, rather than a copy handed out per read. If this ever
        // returns a snapshot the two tests above would silently stop configuring anything.
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

        store.ContinuousErrors.SkipApplyErrors = true;
        store.ContinuousErrors.SkipApplyErrors.ShouldBeTrue();

        store.ContinuousErrors.SkipApplyErrors = false;
        store.ContinuousErrors.SkipApplyErrors.ShouldBeFalse();
    }
}
