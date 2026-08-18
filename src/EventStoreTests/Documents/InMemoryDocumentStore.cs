using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Events.Documents;

namespace EventStoreTests.Documents;

/// <summary>
/// A deliberately naive, in-memory implementation of the jasperfx#647 document contract.
/// </summary>
/// <remarks>
/// <para>
/// Not a product and not shipped — it exists so the shared document compliance suites are actually
/// executed by something before Marten, Polecat and Fisher enroll in them. A compliance suite nobody
/// has ever run is a liability: it can encode an assertion that no correct store could satisfy, and
/// the first three teams to hit it have no way to tell a suite bug from a product bug.
/// </para>
/// <para>
/// It also stands as the smallest possible proof that the contract is implementable without reaching
/// past it — everything below is dictionaries, <c>AsQueryable()</c> and one query-provider wrapper.
/// </para>
/// </remarks>
public class InMemoryDocumentStore : IDocumentSessionFactory<InMemoryDocumentSession, InMemoryDocumentSession>
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<object, object>> _documents = new();

    /// <summary>
    /// The post-commit listeners this store raises — the reference implementation of jasperfx#679.
    /// </summary>
    /// <remarks>
    /// A plain list, matching the <c>StoreOptions.Listeners</c> that all three products already
    /// expose. The point being demonstrated is that nothing about the contract requires a store to
    /// invent registration machinery: a collection of <see cref="IDocumentCommitListener" /> and a
    /// loop after the commit is the whole of it.
    /// </remarks>
    public List<IDocumentCommitListener> Listeners { get; } = new();

    public InMemoryDocumentSession LightweightSession() => new(this);

    public InMemoryDocumentSession QuerySession() => new(this);

    IDocumentSessionOperations IDocumentSessionFactory.LightweightSession() => LightweightSession();

    IDocumentReadOperations IDocumentSessionFactory.QuerySession() => QuerySession();

    public void Clear() => _documents.Clear();

    internal ConcurrentDictionary<object, object> StorageFor(Type documentType)
        => _documents.GetOrAdd(documentType, _ => new ConcurrentDictionary<object, object>());

    internal IReadOnlyList<T> SnapshotOf<T>() => StorageFor(typeof(T)).Values.Cast<T>().ToList();

    /// <summary>
    /// Resolve a document's identity from a conventional <c>Id</c> member. The document contract does
    /// not abstract identity configuration, so the simplest convention both products already honor is
    /// enough here.
    /// </summary>
    internal static object IdentityOf<T>(T document)
    {
        var property = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException(
                           $"{typeof(T).FullName} has no public Id property.");

        return property.GetValue(document)
               ?? throw new InvalidOperationException($"{typeof(T).FullName} has a null Id.");
    }
}

/// <summary>
/// A session over <see cref="InMemoryDocumentStore" />. One type serves as both the writable and the
/// read-only session, which is legal — the contract's tiers are about what a caller may do, not about
/// how many classes a store needs.
/// </summary>
public class InMemoryDocumentSession : IDocumentSessionOperations
{
    private readonly InMemoryDocumentStore _store;
    private readonly List<Action<InMemoryChangeSet>> _pending = new();

    internal InMemoryDocumentSession(InMemoryDocumentStore store) => _store = store;

    public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
        => Task.FromResult(load<T>(id));

    public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
        => Task.FromResult(load<T>(id));

    /// <summary>
    /// The strong-typed-identity overload (jasperfx#665). Overriding the contract's default
    /// implementation rather than inheriting it, which is the case worth demonstrating: the default
    /// forwards a boxed <see cref="Guid" /> or <see cref="string" /> and throws on anything else, so
    /// a store only gains the strong-typed half by writing this.
    /// </summary>
    /// <remarks>
    /// It costs nothing here because the storage is already keyed on the boxed identity
    /// <see cref="InMemoryDocumentStore.IdentityOf" /> pulls off the document. A real store has to
    /// route the value through its own value-type registration; the shared assertion is only that a
    /// boxed identity of any shape resolves the same document the typed overloads would.
    /// </remarks>
    public Task<T?> LoadAsync<T>(object id, CancellationToken token = default) where T : notnull
        => Task.FromResult(load<T>(id));

    private T? load<T>(object id) where T : notnull
        => _store.StorageFor(typeof(T)).TryGetValue(id, out var found) ? (T)found : default;

    public IQueryable<T> Query<T>() where T : notnull
        => InMemoryDocumentQueryable<T>.Wrap(_store.SnapshotOf<T>().AsQueryable());

    public void Store<T>(params T[] entities) where T : notnull
    {
        foreach (var entity in entities)
        {
            var id = InMemoryDocumentStore.IdentityOf(entity);
            _pending.Add(changes =>
            {
                var storage = _store.StorageFor(typeof(T));

                // Insert vs update is decided at commit time against what is actually stored, which
                // is the only honest answer a store with no identity map can give. The contract
                // deliberately does not hold products to one determination here -- only to the
                // document landing in exactly one of the two collections.
                var existed = storage.ContainsKey(id);
                storage[id] = entity;

                if (existed)
                {
                    changes.RecordUpdated(entity);
                }
                else
                {
                    changes.RecordInserted(entity);
                }
            });
        }
    }

    public void Delete<T>(T entity) where T : notnull
        => deleteById<T>(InMemoryDocumentStore.IdentityOf(entity));

    public void Delete<T>(Guid id) where T : notnull => deleteById<T>(id);

    public void Delete<T>(string id) where T : notnull => deleteById<T>(id);

    private void deleteById<T>(object id) where T : notnull
        => _pending.Add(changes =>
        {
            _store.StorageFor(typeof(T)).TryRemove(id, out _);
            changes.RecordDeleted(typeof(T), id);
        });

    public void DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
    {
        var matches = expression.Compile();
        _pending.Add(changes =>
        {
            var storage = _store.StorageFor(typeof(T));
            foreach (var pair in storage.ToArray())
            {
                if (matches((T)pair.Value))
                {
                    storage.TryRemove(pair.Key, out _);
                    changes.RecordDeleted(typeof(T), pair.Key);
                }
            }
        });
    }

    public async Task SaveChangesAsync(CancellationToken token = default)
    {
        // Before anything is applied, so a cancelled commit leaves the store untouched AND raises no
        // listener. The contract's rule is that the callback happens if and only if the commit
        // succeeded, and a store that applied first would break the second half of that.
        token.ThrowIfCancellationRequested();

        if (_pending.Count == 0)
        {
            // An empty unit of work raises nothing. The contract permits either answer here and the
            // compliance suite asserts neither; this side is chosen to match Fisher.
            return;
        }

        var changes = new InMemoryChangeSet();

        foreach (var change in _pending)
        {
            change(changes);
        }

        _pending.Clear();

        foreach (var listener in _store.Listeners)
        {
            await listener.AfterCommitAsync(this, changes, token).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _pending.Clear();
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>
/// The reference <see cref="IDocumentChangeSet" /> — three materialized lists and nothing else.
/// </summary>
/// <remarks>
/// Materialized at commit time rather than wrapping the session's pending work, which is the whole
/// substance of the contract's snapshot rule. A store that handed out a live view would answer
/// correctly inside the callback and empty afterwards; building the lists here is what lets a
/// listener stash the change set and read it later, and what makes a counterpart to Marten's
/// <c>IChangeSet.Clone()</c> unnecessary.
/// </remarks>
internal class InMemoryChangeSet : IDocumentChangeSet
{
    private readonly List<object> _inserted = new();
    private readonly List<object> _updated = new();
    private readonly List<IDocumentDeletion> _deleted = new();

    public IReadOnlyList<object> Inserted => _inserted;

    public IReadOnlyList<object> Updated => _updated;

    public IReadOnlyList<IDocumentDeletion> Deleted => _deleted;

    internal void RecordInserted(object document) => _inserted.Add(document);

    internal void RecordUpdated(object document) => _updated.Add(document);

    internal void RecordDeleted(Type documentType, object? id)
        => _deleted.Add(new InMemoryDeletion(documentType, id));
}

/// <summary>
/// The reference <see cref="IDocumentDeletion" /> — type and identity, no document instance.
/// </summary>
internal record InMemoryDeletion(Type DocumentType, object? Id) : IDocumentDeletion;

/// <summary>
/// Wraps an <see cref="IQueryable{T}" /> so that every LINQ operator applied to it keeps returning a
/// queryable whose provider implements <see cref="IDocumentQueryExecutor" />.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what a store owes the async terminators, and the reason the hook hangs off
/// the provider: <see cref="IQueryable.Provider" /> is what <c>System.Linq.Queryable</c> threads
/// through <c>Where</c> / <c>Select</c> / <c>OrderBy</c>, so wrapping it once at
/// <c>Query&lt;T&gt;()</c> survives an arbitrarily long chain.
/// </para>
/// <para>
/// It implements <see cref="IOrderedQueryable{T}" />, not merely <see cref="IQueryable{T}" />,
/// because <c>Queryable.OrderBy</c> hard-casts its provider's <c>CreateQuery&lt;T&gt;</c> result to
/// <see cref="IOrderedQueryable{T}" /> — a queryable wrapper that only implements
/// <see cref="IQueryable{T}" /> throws <see cref="InvalidCastException" /> on the first
/// <c>OrderBy</c>. This is a constraint of <c>System.Linq</c> rather than of the document contract,
/// but any store wrapping its queryable has to satisfy it, so it is called out here.
/// </para>
/// </remarks>
internal class InMemoryDocumentQueryable<T> : IOrderedQueryable<T>
{
    private readonly IQueryable<T> _inner;

    internal InMemoryDocumentQueryable(InMemoryDocumentQueryProvider provider, IQueryable<T> inner)
    {
        Provider = provider;
        _inner = inner;
    }

    internal static IQueryable<T> Wrap(IQueryable<T> inner)
        => new InMemoryDocumentQueryable<T>(new InMemoryDocumentQueryProvider(inner.Provider), inner);

    public Type ElementType => _inner.ElementType;

    public Expression Expression => _inner.Expression;

    public IQueryProvider Provider { get; }

    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal class InMemoryDocumentQueryProvider : IQueryProvider, IDocumentQueryExecutor
{
    private readonly IQueryProvider _inner;

    internal InMemoryDocumentQueryProvider(IQueryProvider inner) => _inner = inner;

    public IQueryable CreateQuery(Expression expression) => _inner.CreateQuery(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new InMemoryDocumentQueryable<TElement>(this, _inner.CreateQuery<TElement>(expression));

    public object? Execute(Expression expression) => _inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

    public Task<IReadOnlyList<TDoc>> ExecuteToListAsync<TDoc>(IQueryable<TDoc> queryable, CancellationToken token)
        => Task.FromResult<IReadOnlyList<TDoc>>(queryable.ToList());

    public Task<TDoc?> ExecuteFirstOrDefaultAsync<TDoc>(IQueryable<TDoc> queryable, CancellationToken token)
        => Task.FromResult<TDoc?>(queryable.FirstOrDefault());

    public Task<int> ExecuteCountAsync<TDoc>(IQueryable<TDoc> queryable, CancellationToken token)
        => Task.FromResult(queryable.Count());

    public Task<bool> ExecuteAnyAsync<TDoc>(IQueryable<TDoc> queryable, CancellationToken token)
        => Task.FromResult(queryable.Any());
}
