using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Instrumentation events

public record MeterInstalled(string Serial);

public record MeterReadingTaken(int Reading);

#endregion

/// <summary>
/// <see cref="IEventStoreInstrumentation"/> — the opt-in monitoring surface a store exposes to
/// storage-agnostic tooling.
/// </summary>
/// <remarks>
/// <para>
/// Worth pinning for the same reason as the event store explorer surface: this interface exists
/// specifically so an out-of-repo consumer (CritterWatch) can observe a store without referencing
/// concrete store types. That makes it a contract with a consumer neither product's test suite
/// covers, which is exactly the shape of thing that drifts unnoticed.
/// </para>
/// <para>
/// The suite asserts the observer's <em>payload</em> as carefully as its firing. The interface
/// promises each <see cref="IEvent"/> carries what a lifecycle tool needs — event type, stream
/// identity, and timestamp — so a store that invoked the observer with stripped envelopes would
/// satisfy a naive "did it fire" test and still be useless to its only consumer.
/// </para>
/// </remarks>
public abstract class EventStoreInstrumentationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_instrumentation";

        config.AddEventType<MeterInstalled>();
        config.AddEventType<MeterReadingTaken>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// Collects observer callbacks. Synchronized because the observer is invoked from whatever
    /// thread committed the unit of work, and a store is free to commit concurrently — an
    /// unsynchronized <c>List.Add</c> here is a real source of phantom test failures, not a
    /// theoretical one.
    /// </summary>
    private sealed class ObserverLog
    {
        private readonly object _lock = new();
        private readonly List<IReadOnlyList<IEvent>> _batches = new();

        public void Record(IReadOnlyList<IEvent> events)
        {
            lock (_lock)
            {
                _batches.Add(events);
            }
        }

        public IReadOnlyList<IReadOnlyList<IEvent>> Batches
        {
            get
            {
                lock (_lock)
                {
                    return _batches.ToList();
                }
            }
        }

        public IReadOnlyList<IEvent> AllEvents
        {
            get
            {
                lock (_lock)
                {
                    return _batches.SelectMany(x => x).ToList();
                }
            }
        }
    }

    /// <summary>
    /// Attach an observer for the duration of a test and detach it afterwards, so suites sharing a
    /// store cannot see each other's callbacks.
    /// </summary>
    private ObserverLog observe()
    {
        var log = new ObserverLog();
        theFixture.Instrumentation.AppendObserver = log.Record;
        return log;
    }

    private void stopObserving() => theFixture.Instrumentation.AppendObserver = null;

    [Fact]
    public async Task the_append_observer_sees_a_successful_commit()
    {
        var log = observe();

        try
        {
            var streamId = Guid.NewGuid();

            await using var session = OpenSession();
            EventsFor(session).StartStream(streamId, new MeterInstalled("A-1"), new MeterReadingTaken(10));
            await SaveChangesAsync(session);

            log.AllEvents.Count.ShouldBe(2);
        }
        finally
        {
            stopObserving();
        }
    }

    [Fact]
    public async Task the_observed_events_carry_the_metadata_a_lifecycle_consumer_needs()
    {
        var log = observe();

        try
        {
            var streamId = Guid.NewGuid();

            await using var session = OpenSession();
            EventsFor(session).StartStream(streamId, new MeterInstalled("A-2"));
            await SaveChangesAsync(session);

            var observed = log.AllEvents.Single();

            observed.StreamId.ShouldBe(streamId);
            observed.EventType.ShouldBe(typeof(MeterInstalled));
            observed.EventTypeName.ShouldBe(EventTypeNameFor<MeterInstalled>());
            observed.Data.ShouldBeOfType<MeterInstalled>().Serial.ShouldBe("A-2");
            observed.Timestamp.ShouldNotBe(default);
            observed.Version.ShouldBe(1);
        }
        finally
        {
            stopObserving();
        }
    }

    [Fact]
    public async Task appends_across_two_commits_arrive_as_two_batches()
    {
        var log = observe();

        try
        {
            var streamId = Guid.NewGuid();

            await using (var session = OpenSession())
            {
                EventsFor(session).StartStream(streamId, new MeterInstalled("A-3"));
                await SaveChangesAsync(session);
            }

            await using (var session = OpenSession())
            {
                EventsFor(session).Append(streamId, new MeterReadingTaken(42));
                await SaveChangesAsync(session);
            }

            // The observer is per unit of work, not per event -- a consumer recording "appends"
            // edges needs the commit boundary, not a flattened stream.
            log.Batches.Count.ShouldBe(2);
            log.Batches[0].Count.ShouldBe(1);
            log.Batches[1].Count.ShouldBe(1);
        }
        finally
        {
            stopObserving();
        }
    }

    [Fact]
    public async Task a_commit_with_no_events_does_not_notify_the_observer()
    {
        var log = observe();

        try
        {
            await using var session = OpenSession();
            await SaveChangesAsync(session);

            log.Batches.ShouldBeEmpty();
        }
        finally
        {
            stopObserving();
        }
    }

    [Fact]
    public async Task a_detached_observer_stops_receiving_appends()
    {
        var log = observe();
        stopObserving();

        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new MeterInstalled("A-4"));
        await SaveChangesAsync(session);

        log.Batches.ShouldBeEmpty();
    }

    [Fact]
    public void extended_progression_tracking_round_trips_through_the_shared_surface()
    {
        var instrumentation = theFixture.Instrumentation;
        var original = instrumentation.ExtendedProgressionEnabled;

        try
        {
            instrumentation.ExtendedProgressionEnabled = true;
            instrumentation.ExtendedProgressionEnabled.ShouldBeTrue();

            instrumentation.ExtendedProgressionEnabled = false;
            instrumentation.ExtendedProgressionEnabled.ShouldBeFalse();
        }
        finally
        {
            instrumentation.ExtendedProgressionEnabled = original;
        }
    }
}
