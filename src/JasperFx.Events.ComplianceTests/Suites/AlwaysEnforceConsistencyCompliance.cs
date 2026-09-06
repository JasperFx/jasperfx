using System;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region AlwaysEnforceConsistency events and aggregates

public record ManifestOpened(string Destination);

public record ManifestItemAdded(int Count);

public record ManifestSealed;

/// <summary>
/// Guid-keyed aggregate for the flag. Deliberately minimal — nothing here is about aggregation.
/// </summary>
public partial class ComplianceManifest
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;
    public int Parcels { get; set; }
    public bool Dispatched { get; set; }

    public static ComplianceManifest Create(ManifestOpened e) => new() { Destination = e.Destination };

    public void Apply(ManifestItemAdded e) => Parcels += e.Count;

    public void Apply(ManifestSealed _) => Dispatched = true;
}

/// <summary>
/// The string-keyed twin. Stream identity is a store-level setting, so the string half needs its
/// own store and therefore its own aggregate type.
/// </summary>
public partial class ComplianceManifestByKey
{
    public string Id { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public int Parcels { get; set; }

    public static ComplianceManifestByKey Create(ManifestOpened e) => new() { Destination = e.Destination };

    public void Apply(ManifestItemAdded e) => Parcels += e.Count;
}

#endregion

/// <summary>
/// <see cref="IEventStream{T}.AlwaysEnforceConsistency"/> — assert the stream version at commit time
/// even when the handler decided not to append anything.
/// </summary>
/// <remarks>
/// <para>
/// The flag is declared on the shared <see cref="IEventStream{T}"/> and carried on the shared
/// <see cref="StreamAction"/>, so it is contract rather than product surface, and this suite needs no
/// seam member of its own.
/// </para>
/// <para>
/// It is a separate suite from
/// <see cref="FetchForWritingCompliance{TFixture,TOperations,TQuerySession}"/> rather than more facts
/// bolted onto it, because the interesting half is the case that suite structurally cannot reach: a
/// unit of work with <em>no events in it</em>. Every concurrency assertion over there appends
/// something first, and appending is what makes the ordinary version check fire — so a store could
/// ignore this flag completely and pass all of `FetchForWritingCompliance`. That is exactly the
/// silent gap the flag exists to close: a decider that reads a stream, decides to emit nothing, and
/// still needs to know its read was not stale.
/// </para>
/// <para>
/// The failure is asserted as the shared <see cref="ConcurrencyException"/> base rather than a
/// specific derived type. Marten's own tests name <c>ConcurrencyException</c> and Polecat's name
/// <c>EventStreamUnexpectedMaxEventIdException</c>, which derives from it; both are correct and
/// pinning either one would encode a product's choice as the contract.
/// </para>
/// </remarks>
public abstract class AlwaysEnforceConsistencyCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_enforce_consistency";
        config.Snapshot<ComplianceManifest>(SnapshotLifecycle.Inline);
    };

    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_enforce_consistency_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.Snapshot<ComplianceManifestByKey>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<Guid> aManifestAsync(params object[] extra)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        object[] events = [new ManifestOpened("Bree"), .. extra];
        EventsFor(session).StartStream<ComplianceManifest>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<string> aManifestByKeyAsync(params object[] extra)
    {
        var key = $"shipment/{Guid.NewGuid():N}";

        await using var session = OpenSession();
        object[] events = [new ManifestOpened("Bree"), .. extra];
        EventsFor(session).StartStream<ComplianceManifestByKey>(key, events);
        await SaveChangesAsync(session);

        return key;
    }

    /// <summary>
    /// Append to a stream from a session other than the one under test, which is what makes the
    /// version the suite fetched go stale.
    /// </summary>
    private async Task sneakInAnEventAsync(Guid streamId)
    {
        await using var other = OpenSession();
        EventsFor(other).Append(streamId, new ManifestItemAdded(1));
        await SaveChangesAsync(other);
    }

    private async Task sneakInAnEventAsync(string streamKey)
    {
        await using var other = OpenSession();
        EventsFor(other).Append(streamKey, new ManifestItemAdded(1));
        await SaveChangesAsync(other);
    }

    [Fact]
    public async Task the_default_is_off()
    {
        var streamId = await aManifestAsync();

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);

        stream.AlwaysEnforceConsistency.ShouldBeFalse();
    }

    [Fact]
    public async Task the_default_is_off_for_string_identity()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await aManifestByKeyAsync();

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceManifestByKey>(key, Cancellation);

        stream.AlwaysEnforceConsistency.ShouldBeFalse();
    }

    /// <summary>
    /// The baseline the flag is measured against: with it off, a stream fetched for writing and then
    /// left alone commits an empty unit of work silently, however far the stream has moved on.
    /// </summary>
    [Fact]
    public async Task with_the_flag_off_an_empty_unit_of_work_does_not_check_the_version()
    {
        var streamId = await aManifestAsync(new ManifestItemAdded(2));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);
        stream.AlwaysEnforceConsistency.ShouldBeFalse();

        await sneakInAnEventAsync(streamId);

        // No events appended, flag off: nothing to check, nothing to throw.
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task with_the_flag_on_and_no_drift_an_empty_unit_of_work_still_commits()
    {
        var streamId = await aManifestAsync(new ManifestItemAdded(2), new ManifestSealed());

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);
        stream.AlwaysEnforceConsistency = true;

        // Nobody moved the stream, so the assertion holds and the commit succeeds.
        await SaveChangesAsync(session);

        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }

    /// <summary>
    /// The load-bearing fact. Nothing is appended, so only the flag can produce a failure.
    /// </summary>
    [Fact]
    public async Task with_the_flag_on_an_empty_unit_of_work_fails_on_version_drift()
    {
        var streamId = await aManifestAsync(new ManifestItemAdded(2));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);
            stream.AlwaysEnforceConsistency = true;

            await sneakInAnEventAsync(streamId);

            await SaveChangesAsync(session);
        });
    }

    [Fact]
    public async Task with_the_flag_on_and_events_present_the_write_still_lands()
    {
        var streamId = await aManifestAsync(new ManifestItemAdded(2));

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);
            stream.AlwaysEnforceConsistency = true;
            stream.AppendOne(new ManifestItemAdded(3));

            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var shipment = await LoadDocumentAsync<ComplianceManifest>(reader, streamId);
        shipment.ShouldNotBeNull();
        shipment.Parcels.ShouldBe(5);
    }

    /// <summary>
    /// Turning the flag on must not <em>replace</em> the ordinary version check that appending
    /// already performs — a store that routed the flag down a separate code path could lose it.
    /// </summary>
    [Fact]
    public async Task with_the_flag_on_and_events_present_the_version_is_still_checked()
    {
        var streamId = await aManifestAsync(new ManifestItemAdded(2));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            var stream = await EventsFor(session).FetchForWriting<ComplianceManifest>(streamId, Cancellation);
            stream.AlwaysEnforceConsistency = true;
            stream.AppendOne(new ManifestItemAdded(3));

            await sneakInAnEventAsync(streamId);

            await SaveChangesAsync(session);
        });
    }

    /// <summary>
    /// A stream that was never created sits at version 0, and the handle's expected version is 0 too,
    /// so the assertion holds — turning the flag on must not turn "does not exist yet" into a
    /// conflict. Both products pin this, and it is the case a naive "is there a row?" implementation
    /// of the check gets wrong.
    /// </summary>
    [Fact]
    public async Task with_the_flag_on_a_never_created_stream_commits()
    {
        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForWriting<ComplianceManifest>(Guid.NewGuid(), Cancellation);

        stream.Aggregate.ShouldBeNull();
        stream.AlwaysEnforceConsistency = true;

        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task with_the_flag_on_and_no_drift_a_string_identified_stream_commits()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await aManifestByKeyAsync(new ManifestItemAdded(2));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceManifestByKey>(key, Cancellation);
        stream.AlwaysEnforceConsistency = true;

        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task with_the_flag_on_a_string_identified_stream_fails_on_version_drift()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await aManifestByKeyAsync(new ManifestItemAdded(2));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            var stream = await EventsFor(session)
                .FetchForWriting<ComplianceManifestByKey>(key, Cancellation);
            stream.AlwaysEnforceConsistency = true;

            await sneakInAnEventAsync(key);

            await SaveChangesAsync(session);
        });
    }

    /// <summary>
    /// The flag lives on the handle, not on the session: a second stream in the same unit of work
    /// that did not opt in must not be dragged into the assertion.
    /// </summary>
    [Fact]
    public async Task the_flag_is_scoped_to_the_stream_that_set_it()
    {
        var enforced = await aManifestAsync(new ManifestItemAdded(2));
        var relaxed = await aManifestAsync(new ManifestItemAdded(2));

        await using var session = OpenSession();

        var relaxedStream = await EventsFor(session).FetchForWriting<ComplianceManifest>(relaxed, Cancellation);
        var enforcedStream = await EventsFor(session).FetchForWriting<ComplianceManifest>(enforced, Cancellation);
        enforcedStream.AlwaysEnforceConsistency = true;

        // Drift on the stream that did NOT opt in.
        await sneakInAnEventAsync(relaxed);

        relaxedStream.ShouldNotBeNull();
        await SaveChangesAsync(session);
    }
}
