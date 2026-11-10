using System.Data.Common;
using System.Linq.Expressions;
using JasperFx;
using JasperFx.Events.Documents;
using Shouldly;

namespace EventTests.Documents;

/// <summary>
/// jasperfx#1052: the bodies a store inherits until it adopts a member. Everything here is a store that has
/// not implemented the member, and the rule under test is that it refuses loudly. A default that quietly
/// did nothing would be a hook that never fires, an outbox that never flushes and a message that is gone.
/// </summary>
public class SessionContractDefaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public class Thing
    {
        public Guid Id { get; set; }
    }

    public readonly record struct ThingId(Guid Value);

    /// <summary>
    /// A session with only the abstract members, which is every store before it adopts.
    /// </summary>
    private class BareSession : IDocumentSessionOperations
    {
        private readonly Thing? _found;

        public BareSession(Thing? found = null) => _found = found;

        public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
            => Task.FromResult(_found is T found && _found.Id == id ? found : default);

        public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
            => Task.FromResult(_found is T found && _found.Id.ToString() == id ? found : default);

        public IQueryable<T> Query<T>() where T : notnull => throw new NotImplementedException();
        public void Store<T>(params T[] entities) where T : notnull => throw new NotImplementedException();
        public void Delete<T>(T entity) where T : notnull => throw new NotImplementedException();
        public void Delete<T>(Guid id) where T : notnull => throw new NotImplementedException();
        public void Delete<T>(string id) where T : notnull => throw new NotImplementedException();

        public void DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
            => throw new NotImplementedException();

        public Task SaveChangesAsync(CancellationToken token = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => default;
    }

    private class SessionWithMetadata : BareSession, IMetadataContext
    {
        public string TenantId => "tenant-a";
        public string? CausationId { get; set; }
        public string? CorrelationId { get; set; }
        public string? CurrentUserName { get; set; }
        public Dictionary<string, object>? Headers => null;
        public bool CausationIdEnabled => true;
        public bool CorrelationIdEnabled => true;
        public bool HeadersEnabled => true;
        public bool UserNameEnabled => true;
    }

    private class Listener : IDocumentSaveListener
    {
        public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
            => Task.CompletedTask;

        public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit, CancellationToken token)
            => Task.CompletedTask;
    }

    private class Participant : IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
            => Task.CompletedTask;
    }

    private static void shouldBeRefusedNaming<TImplementer>(Action act)
    {
        var refused = Should.Throw<NotSupportedException>(act);
        refused.Message.ShouldContain(typeof(TImplementer).Name);
    }

    [Fact]
    public void a_store_without_hooks_refuses_them_when_they_are_registered()
    {
        IDocumentSessionOperations session = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => session.AddSaveListener(new Listener()));
    }

    [Fact]
    public void a_store_without_shared_participants_refuses_one_when_it_is_enlisted()
    {
        IDocumentSessionOperations session = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => session.AddTransactionParticipant(new Participant()));
    }

    [Fact]
    public void the_participant_refusal_reaches_the_write_tier_a_subscription_holds()
    {
        IDocumentWriteOperations operations = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => operations.AddTransactionParticipant(new Participant()));
    }

    [Fact]
    public void insert_refuses_rather_than_falling_back_to_an_upsert()
    {
        IDocumentSessionOperations session = new BareSession();

        // BareSession.Store throws NotImplementedException, so reaching it would fail this differently
        shouldBeRefusedNaming<BareSession>(() => session.Insert(new Thing()));
    }

    [Fact]
    public void update_refuses_rather_than_falling_back_to_an_upsert()
    {
        IDocumentSessionOperations session = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => session.Update(new Thing()));
    }

    [Fact]
    public void store_objects_refuses_on_a_store_that_has_not_implemented_it()
    {
        IDocumentSessionOperations session = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => session.StoreObjects([new Thing()]));
    }

    [Fact]
    public void metadata_context_needs_no_store_change_on_a_session_class_that_already_has_it()
    {
        // All three stores: their session classes are IMetadataContext through Weasel's IStorageSession,
        // and only their public interfaces leave it out
        var session = new SessionWithMetadata();
        IDocumentSessionOperations shared = session;

        shared.MetadataContext.ShouldBeSameAs(session);
        shared.MetadataContext.TenantId.ShouldBe("tenant-a");

        shared.MetadataContext.CausationId = "cause";
        session.CausationId.ShouldBe("cause");
    }

    [Fact]
    public void metadata_context_refuses_on_a_session_that_is_not_one()
    {
        IDocumentSessionOperations session = new BareSession();

        shouldBeRefusedNaming<BareSession>(() => _ = session.MetadataContext);
    }

    [Fact]
    public async Task check_exists_answers_through_load_for_a_store_that_has_not_implemented_it()
    {
        var thing = new Thing { Id = Guid.NewGuid() };
        IDocumentReadOperations session = new BareSession(thing);

        (await session.CheckExistsAsync<Thing>(thing.Id, Token)).ShouldBeTrue();
        (await session.CheckExistsAsync<Thing>(thing.Id.ToString(), Token)).ShouldBeTrue();
        (await session.CheckExistsAsync<Thing>(Guid.NewGuid(), Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task check_exists_refuses_a_strong_typed_identity_the_store_cannot_load()
    {
        // The same limit as LoadAsync(object): false here would read as "no such document"
        IDocumentReadOperations session = new BareSession();

        var refused = await Should.ThrowAsync<NotSupportedException>(
            () => session.CheckExistsAsync<Thing>(new ThingId(Guid.NewGuid()), Token));

        refused.Message.ShouldContain(nameof(BareSession));
    }

    [Fact]
    public async Task check_exists_refuses_a_null_identity()
    {
        IDocumentReadOperations session = new BareSession();

        await Should.ThrowAsync<ArgumentNullException>(() => session.CheckExistsAsync<Thing>(null!, Token));
    }
}
