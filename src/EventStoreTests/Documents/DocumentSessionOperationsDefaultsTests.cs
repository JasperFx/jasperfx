using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// The default implementation of <see cref="IDocumentSessionOperations.PendingStreams" />
/// (jasperfx#673).
/// </summary>
/// <remarks>
/// <see cref="InMemoryDocumentStore" /> is a document-only reference implementation with no event
/// store behind it, so it leaves this member on the default and never enrolls
/// <c>PendingStreamActionsCompliance</c> — which is exactly why the default needs its own test. What
/// is asserted is that it throws rather than answering empty: an empty list is indistinguishable
/// from a session with nothing pending, so a silent default would leave a consumer's work quietly
/// discarded on a store that had not implemented the member.
/// </remarks>
public class DocumentSessionOperationsDefaultsTests
{
    private readonly IDocumentSessionOperations theSession = new NotYetOverriddenSession();

    [Fact]
    public void pending_streams_throws_rather_than_answering_with_an_empty_list()
    {
        var ex = Should.Throw<NotSupportedException>(() => theSession.PendingStreams);

        ex.Message.ShouldContain(typeof(NotYetOverriddenSession).FullName!);
        ex.Message.ShouldContain(nameof(IDocumentSessionOperations.PendingStreams));
    }

    /// <summary>
    /// Implements only what the contract has no default for, so it is the shape a store has on the
    /// day it takes the JasperFx bump and before it writes the forwarding member.
    /// </summary>
    private class NotYetOverriddenSession : IDocumentSessionOperations
    {
        public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
            => throw new NotImplementedException();

        public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
            => throw new NotImplementedException();

        public IQueryable<T> Query<T>() where T : notnull => throw new NotImplementedException();

        public void Store<T>(params T[] entities) where T : notnull => throw new NotImplementedException();

        public void Delete<T>(T entity) where T : notnull => throw new NotImplementedException();

        public void Delete<T>(Guid id) where T : notnull => throw new NotImplementedException();

        public void Delete<T>(string id) where T : notnull => throw new NotImplementedException();

        public void DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
            => throw new NotImplementedException();

        public Task SaveChangesAsync(CancellationToken token = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => default;
    }
}
