using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Tags;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Tombstoning streams

public record DossierOpened(string Subject);

public record DossierNoteAdded(string Note);

/// <summary>
/// The tag the tombstoning suite puts on its events, so a fact can prove the tag rows go with the
/// events. Its own type rather than a DCB suite's, so the tag table belongs to this suite's schema.
/// </summary>
public record DossierId(Guid Value);

/// <summary>
/// Exists only so the suite's tag type has an aggregate to bound, the shape every DCB registration in
/// these suites takes.
/// </summary>
public partial class ComplianceDossier
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int Notes { get; set; }

    public void Apply(DossierOpened e) => Subject = e.Subject;

    public void Apply(DossierNoteAdded e) => Notes++;
}

#endregion

/// <summary>
/// <c>TombstoneStream</c>: the hard delete of a stream and every event in it (jasperfx#1014).
/// </summary>
/// <remarks>
/// <para>
/// The operation exists so a consumer can purge data without knowing which store it is talking to —
/// Stoat's plan purge was the first, and carried its own per-store seam until this was on the shared
/// <see cref="IEventStoreOperations"/>. So the facts pin what such a caller relies on rather than how
/// any one store spells the delete: what is gone, what is untouched, what the identity and the
/// sequence are good for afterwards.
/// </para>
/// <para>
/// Every fact is gated on <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsTombstoneStream"/>,
/// because the contract members default to throwing.
/// </para>
/// </remarks>
public abstract class StreamTombstoningCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "tombstone_a";
    private const string TenantB = "tombstone_b";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_tombstoning";
        config.AddEventType<DossierOpened>();
        config.AddEventType<DossierNoteAdded>();
        config.RegisterTagType<DossierId>("dossier", typeof(ComplianceDossier));
        config.LiveAggregation<ComplianceDossier>();
    };

    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_tombstoning_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.AddEventType<DossierOpened>();
        config.AddEventType<DossierNoteAdded>();
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_tombstoning_conjoined";
        config.ConjoinedEventTenancy = true;
        config.AddEventType<DossierOpened>();
        config.AddEventType<DossierNoteAdded>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void skipUnlessSupported() => Assert.SkipUnless(theFixture.SupportsTombstoneStream,
        "This store does not implement IEventStoreOperations.TombstoneStream.");

    private async Task<Guid> aDossierAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            new DossierOpened("Purge me"),
            new DossierNoteAdded("one"),
            new DossierNoteAdded("two"));
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task tombstoneAsync(Guid streamId)
    {
        await using var session = OpenSession();
        EventsFor(session).TombstoneStream(streamId);
        await SaveChangesAsync(session);
    }

    private async Task shouldBeGoneAsync(object streamIdentity)
    {
        await using var session = OpenSession();
        var events = EventsFor(session);

        if (streamIdentity is Guid id)
        {
            (await events.FetchStreamStateAsync(id, Cancellation)).ShouldBeNull();
            (await events.FetchStreamAsync(id, token: Cancellation)).ShouldBeEmpty();
        }
        else
        {
            var key = (string)streamIdentity;
            (await events.FetchStreamStateAsync(key, Cancellation)).ShouldBeNull();
            (await events.FetchStreamAsync(key, token: Cancellation)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task tombstoning_deletes_the_stream_and_its_events()
    {
        skipUnlessSupported();

        var streamId = await aDossierAsync();

        await tombstoneAsync(streamId);

        await shouldBeGoneAsync(streamId);
    }

    [Fact]
    public async Task tombstoning_is_queued_until_save_changes()
    {
        skipUnlessSupported();

        var streamId = await aDossierAsync();

        await using (var session = OpenSession())
        {
            EventsFor(session).TombstoneStream(streamId);

            // Not saved, and never will be: the session is disposed with the delete still queued.
            await using var reader = OpenSession();
            (await EventsFor(reader).FetchStreamAsync(streamId, token: Cancellation)).Count.ShouldBe(3);
        }

        await using var after = OpenSession();
        var state = await EventsFor(after).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task tombstoning_one_stream_leaves_its_neighbours_alone()
    {
        skipUnlessSupported();

        var doomed = await aDossierAsync();
        var untouched = await aDossierAsync();

        await tombstoneAsync(doomed);

        await shouldBeGoneAsync(doomed);

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(untouched, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
        (await EventsFor(session).FetchStreamAsync(untouched, token: Cancellation)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task tombstoning_a_stream_that_does_not_exist_is_not_an_error()
    {
        skipUnlessSupported();

        var unknown = Guid.NewGuid();

        await tombstoneAsync(unknown);

        await shouldBeGoneAsync(unknown);
    }

    /// <summary>
    /// Tag rows are the part a store gets wrong: they reference the events, so deleting the events
    /// first either strands them or — where the reference is a real foreign key, as in Fisher — fails
    /// the whole unit of work, which made any tagged stream impossible to delete.
    /// </summary>
    [Fact]
    public async Task tombstoning_a_tagged_stream_deletes_its_tags()
    {
        skipUnlessSupported();

        var streamId = Guid.NewGuid();
        var doomedTag = new DossierId(Guid.NewGuid());
        var keptTag = new DossierId(Guid.NewGuid());

        await using (var session = OpenSession())
        {
            var opened = EventsFor(session).BuildEvent(new DossierOpened("Tagged"));
            opened.WithTag(doomedTag);
            var noted = EventsFor(session).BuildEvent(new DossierNoteAdded("tagged"));
            noted.WithTag(doomedTag);
            EventsFor(session).StartStream(streamId, opened, noted);

            var neighbour = EventsFor(session).BuildEvent(new DossierOpened("Neighbour"));
            neighbour.WithTag(keptTag);
            EventsFor(session).StartStream(Guid.NewGuid(), neighbour);

            await SaveChangesAsync(session);
        }

        await tombstoneAsync(streamId);

        await shouldBeGoneAsync(streamId);

        await using var reader = OpenSession();
        (await EventsFor(reader).QueryByTagsAsync(new EventTagQuery().Or<DossierId>(doomedTag), Cancellation))
            .ShouldBeEmpty();
        (await EventsFor(reader).EventsExistAsync(new EventTagQuery().Or<DossierId>(doomedTag), Cancellation))
            .ShouldBeFalse();
        (await EventsFor(reader).QueryByTagsAsync(new EventTagQuery().Or<DossierId>(keptTag), Cancellation))
            .Count.ShouldBe(1);
    }

    /// <summary>
    /// A hard delete is not a soft delete with a stream row left behind: the identity is free again.
    /// </summary>
    [Fact]
    public async Task a_tombstoned_stream_identity_can_start_a_new_stream()
    {
        skipUnlessSupported();

        var streamId = await aDossierAsync();
        await tombstoneAsync(streamId);

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, new DossierOpened("Second life"));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        var state = await EventsFor(reader).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(1);

        var events = await EventsFor(reader).FetchStreamAsync(streamId, token: Cancellation);
        events.Single().Data.ShouldBeOfType<DossierOpened>().Subject.ShouldBe("Second life");
    }

    /// <summary>
    /// The deleted sequence numbers stay spent. Were they reused, an async projection whose
    /// high-water mark had already passed them would never see the events that took them.
    /// </summary>
    [Fact]
    public async Task the_sequence_numbers_of_a_tombstoned_stream_are_not_reused()
    {
        skipUnlessSupported();

        var streamId = await aDossierAsync();

        long highestDeleted;
        await using (var reader = OpenSession())
        {
            highestDeleted = (await EventsFor(reader).FetchStreamAsync(streamId, token: Cancellation))
                .Max(x => x.Sequence);
        }

        await tombstoneAsync(streamId);

        var next = await aDossierAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(next, token: Cancellation);
        events.ShouldAllBe(x => x.Sequence > highestDeleted);
    }

    [Fact]
    public async Task tombstoning_a_string_keyed_stream()
    {
        skipUnlessSupported();

        await theFixture.ConfigureAsync(_stringConfiguration);
        await theFixture.CleanEventDataAsync();

        var doomed = "dossier-" + Guid.NewGuid();
        var untouched = "dossier-" + Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(doomed, new DossierOpened("Purge me"), new DossierNoteAdded("one"));
            EventsFor(session).StartStream(untouched, new DossierOpened("Keep me"));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).TombstoneStream(doomed);
            await SaveChangesAsync(session);
        }

        await shouldBeGoneAsync(doomed);

        await using var reader = OpenSession();
        (await EventsFor(reader).FetchStreamAsync(untouched, token: Cancellation)).Count.ShouldBe(1);
    }

    /// <summary>
    /// Under conjoined tenancy the same stream id can exist once per tenant, and the delete is scoped
    /// to the session's tenant — a purge for one tenant must not reach another's data.
    /// </summary>
    [Fact]
    public async Task tombstoning_under_conjoined_tenancy_deletes_only_the_sessions_tenant()
    {
        skipUnlessSupported();
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This store does not support conjoined event tenancy.");

        await theFixture.ConfigureAsync(_conjoinedConfiguration);
        await theFixture.CleanEventDataAsync();

        var streamId = Guid.NewGuid();

        foreach (var tenantId in new[] { TenantA, TenantB })
        {
            await using var session = await openForTenantAsync(tenantId);
            EventsFor(session).StartStream(streamId, new DossierOpened(tenantId), new DossierNoteAdded("one"));
            await SaveChangesAsync(session);
        }

        await using (var session = await openForTenantAsync(TenantA))
        {
            EventsFor(session).TombstoneStream(streamId);
            await SaveChangesAsync(session);
        }

        await using (var tenantA = await openForTenantAsync(TenantA))
        {
            (await EventsFor(tenantA).FetchStreamAsync(streamId, token: Cancellation)).ShouldBeEmpty();
            (await EventsFor(tenantA).FetchStreamStateAsync(streamId, Cancellation)).ShouldBeNull();
        }

        await using var tenantB = await openForTenantAsync(TenantB);
        var events = await EventsFor(tenantB).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<DossierOpened>().Subject.ShouldBe(TenantB);
    }

    private async Task<TOperations> openForTenantAsync(string tenantId)
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;
        var database = (await theFixture.EventStore.AllDatabases()).First();
        return store.OpenSession(database, tenantId);
    }
}
