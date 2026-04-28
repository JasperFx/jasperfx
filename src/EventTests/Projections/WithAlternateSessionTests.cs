using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// Tests for SliceGroup.EntityStep.WithAlternateSession — the hook that enables
/// enrichment from ancillary stores (or any IStorageOperations other than the primary).
/// </summary>
public class WithAlternateSessionTests
{
    private readonly SliceGroup<LetterCounts, Guid> theGroup;
    private readonly PrimarySession primarySession = new();
    private readonly AncillarySession ancillarySession = new();

    public WithAlternateSessionTests()
    {
        theGroup = new SliceGroup<LetterCounts, Guid>();
        theGroup.Operations = primarySession;
    }

    private Guid AddSlice(string text, string userName)
    {
        var events = text.ToLetterEventsWithWrapper();
        if (userName.IsNotEmpty())
            events = events.Concat([Event.For(new Assigned(userName))]);
        var slice = new EventSlice<LetterCounts, Guid>(Guid.NewGuid(), StorageConstants.DefaultTenantId, events);
        theGroup.Slices[slice.Id] = slice;
        return slice.Id;
    }

    [Fact]
    public async Task alternate_session_storage_is_used_instead_of_primary()
    {
        var id = AddSlice("AAABCCDDDD", "Bill");
        // Only ancillary knows about Bill — primary has no data
        ancillarySession.Users["Bill"] = new User("Bill", "William");

        await theGroup.EnrichWith<User>()
            .WithAlternateSession(ancillarySession)
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .EnrichAsync((slice, e, user) =>
                slice.ReplaceEvent(e, new AssignedToUser(user)));

        theGroup.Slices[id].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName
            .ShouldBe("Bill");
        ancillarySession.FetchStorageWasCalled.ShouldBeTrue();
        primarySession.FetchStorageWasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task alternate_session_is_disposed_after_enrichment_completes()
    {
        AddSlice("AAABCCDDDD", "Bill");
        ancillarySession.Users["Bill"] = new User("Bill", "William");

        await theGroup.EnrichWith<User>()
            .WithAlternateSession(ancillarySession)
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .EnrichAsync((slice, e, user) => { });

        ancillarySession.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task alternate_session_is_disposed_even_when_no_matching_events()
    {
        // Slice with no Assigned events
        AddSlice("AAABCCDDDD", "");

        await theGroup.EnrichWith<User>()
            .WithAlternateSession(ancillarySession)
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .EnrichAsync((slice, e, user) => { });

        ancillarySession.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task primary_session_is_not_disposed_by_normal_enrichment()
    {
        AddSlice("AAABCCDDDD", "Bill");
        primarySession.Users["Bill"] = new User("Bill", "William");

        await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .EnrichAsync((slice, e, user) => { });

        primarySession.WasDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task add_references_also_works_via_alternate_session()
    {
        var id = AddSlice("AAABCCDDDD", "Bill");
        ancillarySession.Users["Bill"] = new User("Bill", "William");

        await theGroup.EnrichWith<User>()
            .WithAlternateSession(ancillarySession)
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .AddReferences();

        theGroup.Slices[id].Events().OfType<IEvent<References<User>>>().ShouldNotBeEmpty();
        ancillarySession.WasDisposed.ShouldBeTrue();
    }

    // ── fakes ──────────────────────────────────────────────────────────────────

    private class PrimarySession : FakeStorageOperations
    {
    }

    private class AncillarySession : FakeStorageOperations
    {
    }

    private abstract class FakeStorageOperations : IStorageOperations, IProjectionStorage<User, string>
    {
        public Dictionary<string, User> Users = new();
        public bool WasDisposed;
        public bool FetchStorageWasCalled;

        // IStorageOperations
        ValueTask IAsyncDisposable.DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }

        Task<IProjectionStorage<TDoc, TId>> IStorageOperations.FetchProjectionStorageAsync<TDoc, TId>(
            string tenantId, CancellationToken cancellationToken)
        {
            FetchStorageWasCalled = true;
            return Task.FromResult((IProjectionStorage<TDoc, TId>)(object)this);
        }

        bool IStorageOperations.EnableSideEffectsOnInlineProjections => false;

        ValueTask<IMessageSink> IStorageOperations.GetOrStartMessageSink() =>
            throw new NotImplementedException();

        // IProjectionStorage<User, string>
        string IProjectionStorage<User, string>.TenantId => StorageConstants.DefaultTenantId;

        Task<IReadOnlyDictionary<string, User>> IProjectionStorage<User, string>.LoadManyAsync(
            string[] identities, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, User>>(Users);

        Task<User> IProjectionStorage<User, string>.LoadAsync(string id, CancellationToken cancellation) =>
            throw new NotImplementedException();

        void IIdentitySetter<User, string>.SetIdentity(User document, string identity) =>
            throw new NotImplementedException();

        string IIdentitySetter<User, string>.Identity(User document) => document.UserName;

        void IProjectionStorage<User, string>.HardDelete(User snapshot) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.UnDelete(User snapshot) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.Store(User snapshot) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.Delete(string identity) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.HardDelete(User snapshot, string tenantId) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.UnDelete(User snapshot, string tenantId) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.Store(User snapshot, string id, string tenantId) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.Delete(string identity, string tenantId) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.StoreProjection(User aggregate, IEvent? lastEvent, AggregationScope scope) => throw new NotImplementedException();
        void IProjectionStorage<User, string>.ArchiveStream(string sliceId, string tenantId) => throw new NotImplementedException();
    }
}
