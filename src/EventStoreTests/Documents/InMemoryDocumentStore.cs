using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JasperFx;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Documents;
using JasperFx.Metadata;

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
public partial class InMemoryDocumentStore : IDocumentSessionFactory<InMemoryDocumentSession, InMemoryDocumentSession>
{
    private readonly ConcurrentDictionary<(Type Type, string Tenant), ConcurrentDictionary<object, object>>
        _documents = new();

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

    /// <summary>
    /// The document types this store slices by tenant — the reference replay of
    /// <see cref="DocumentComplianceConfig.ConjoinedDocuments" /> (jasperfx#898).
    /// </summary>
    /// <remarks>
    /// A per-type set rather than a store-wide switch, because that is the shape the config carries
    /// and the shape all three products spell (<c>Schema.For&lt;T&gt;().MultiTenanted()</c>). A type
    /// that is not in here keeps living in the default tenant's bucket however the session was opened,
    /// which is what lets the other document suites go on using <see cref="ComplianceWidget" />
    /// untenanted.
    /// </remarks>
    public HashSet<Type> ConjoinedTypes { get; } = new();

    /// <summary>
    /// Document types declared for <see cref="IVersioned" /> Guid optimistic concurrency — the replay
    /// of <see cref="DocumentComplianceConfig.OptimisticConcurrencyTypes" />.
    /// </summary>
    /// <remarks>
    /// Declared rather than inferred from the marker alone, deliberately: the stores disagree about
    /// whether <c>IVersioned</c> is itself the opt-in or merely supplies the member to guard on, and
    /// the config exists so a suite tests the behaviour instead of that disagreement. Honouring the
    /// declaration is what makes this reference store a fair witness.
    /// </remarks>
    public HashSet<Type> OptimisticConcurrencyTypes { get; } = new();

    /// <summary>
    /// Document members declared as the concurrency version through the store's own metadata mapping
    /// rather than through <see cref="IVersioned" /> — the replay of
    /// <see cref="DocumentComplianceConfig.MappedVersionMembers" /> (polecat#720).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented here rather than left for the products for the jasperfx#903 reason, which that
    /// issue paid for the hard way: a fact that skips everywhere it could be run is a fact nobody has
    /// checked. The mapped route is where this exact field has now been found broken four times
    /// (fisher#245, marten#5372, polecat#592, polecat#720) — including twice after a shared suite
    /// existed for the marker-interface half — so a reference store that cannot exercise it leaves
    /// the new facts in precisely the state that produced those four.
    /// </para>
    /// <para>
    /// A <see cref="MemberInfo" /> per type: the member's own CLR type is what names the mode, which
    /// is the rule Polecat adopted and the one the suite's documentation states.
    /// </para>
    /// </remarks>
    public Dictionary<Type, MemberInfo> MappedVersionMembers { get; } = new();

    public InMemoryDocumentSession LightweightSession() => new(this, StorageConstants.DefaultTenantId);

    public InMemoryDocumentSession QuerySession() => new(this, StorageConstants.DefaultTenantId);

    public InMemoryDocumentSession LightweightSession(string tenantId) => new(this, tenantId);

    public InMemoryDocumentSession QuerySession(string tenantId) => new(this, tenantId);

    IDocumentSessionOperations IDocumentSessionFactory.LightweightSession() => LightweightSession();

    IDocumentReadOperations IDocumentSessionFactory.QuerySession() => QuerySession();

    // ⚠️ The explicit forwarders are not boilerplate. C# interface implementation is not return-type
    // covariant, so the four product-typed members above satisfy only the GENERIC interface; without
    // these the non-generic contract members stay bound to their throwing defaults, and the
    // compliance suites -- which hold the non-generic contract -- fail on a store whose tenancy is
    // perfectly correct. See the remarks on IDocumentSessionFactory.LightweightSession(string).
    IDocumentSessionOperations IDocumentSessionFactory.LightweightSession(string tenantId)
        => LightweightSession(tenantId);

    IDocumentReadOperations IDocumentSessionFactory.QuerySession(string tenantId) => QuerySession(tenantId);

    public void Clear()
    {
        _documents.Clear();
        _metadata.Clear();
    }

    /// <summary>
    /// The bucket a document type's rows live in for one session's tenant. A type that was never
    /// declared conjoined resolves to the default tenant's bucket whatever the session asked for, and a
    /// declared sub-class resolves to its root's bucket — one table per hierarchy.
    /// </summary>
    internal ConcurrentDictionary<object, object> StorageFor(Type documentType, string tenantId)
    {
        var root = RootOf(documentType);
        return _documents.GetOrAdd((root, ScopeFor(root, tenantId)),
            _ => new ConcurrentDictionary<object, object>());
    }

    internal string ScopeFor(Type documentType, string tenantId)
        => ConjoinedTypes.Contains(RootOf(documentType)) ? tenantId : StorageConstants.DefaultTenantId;

    /// <summary>
    /// Every live row a session reading <typeparamref name="T" /> may see: soft-deleted rows hidden, and
    /// in a hierarchy only the rows that are a <typeparamref name="T" />.
    /// </summary>
    internal IReadOnlyList<T> SnapshotOf<T>(string tenantId)
    {
        var scope = ScopeFor(typeof(T), tenantId);
        return StorageFor(typeof(T), tenantId)
            .Where(pair => pair.Value is T && !IsDeleted(typeof(T), scope, pair.Key))
            .Select(pair => Copy<T>(pair.Value))
            .ToList();
    }

    /// <summary>
    /// A fresh instance of a stored document, standing in for the serialize-in / deserialize-out round
    /// trip every real document store performs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found while implementing the Guid concurrency guard for jasperfx#903, and it was a real defect
    /// in this double rather than a detail. Handing back the <em>stored reference</em> made two
    /// independent loads the same object, so any fact about two callers reading one row separately was
    /// meaningless here — mutating one mutated the other, and they could never disagree about a
    /// version. <c>a_stale_instance_is_refused_and_the_winner_stands</c> is unsatisfiable against a
    /// store that does this, however correct its guard.
    /// </para>
    /// <para>
    /// Applied on write as well as read, for the mirror-image reason: a real store serializes at
    /// commit, so a caller mutating its instance afterwards must not silently rewrite stored state.
    /// The version write-back still lands on the caller's own instance, before the copy is taken —
    /// which is the contract
    /// <c>GuidOptimisticConcurrencyCompliance.a_successful_write_moves_the_instances_own_version_on</c>
    /// pins.
    /// </para>
    /// <para>
    /// JSON is the mechanism because it is what the products actually do, and because it keeps the
    /// double honest about what survives a round trip.
    /// </para>
    /// <para>
    /// Round-tripped as the document's <em>runtime</em> type rather than <typeparamref name="T" />, so a
    /// sub-class stored or read through its root keeps its sub-class members (jasperfx#870).
    /// </para>
    /// </remarks>
    internal static T Copy<T>(object document)
        => (T)JsonSerializer.Deserialize(JsonSerializer.Serialize(document, document.GetType()), document.GetType())!;

    /// <summary>
    /// Every row of a type across every tenant — what the fixture's <c>AnyTenant</c> seam forwards to.
    /// </summary>
    internal IReadOnlyList<T> SnapshotAcrossTenants<T>()
        => _documents.Where(pair => pair.Key.Type == typeof(T))
            .SelectMany(pair => pair.Value.Values)
            .OfType<T>()
            .ToList();

    /// <summary>
    /// Every row of a type belonging to any of the named tenants — the <c>TenantIsOneOf</c> seam.
    /// </summary>
    internal IReadOnlyList<T> SnapshotForTenants<T>(IEnumerable<string> tenantIds)
    {
        var scopes = tenantIds.Select(x => ScopeFor(typeof(T), x)).ToHashSet();

        return _documents.Where(pair => pair.Key.Type == typeof(T) && scopes.Contains(pair.Key.Tenant))
            .SelectMany(pair => pair.Value.Values)
            .Select(Copy<T>)
            .ToList();
    }

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
    private readonly string _tenantId;
    private readonly List<Action<InMemoryChangeSet>> _pending = new();

    internal InMemoryDocumentSession(InMemoryDocumentStore store, string tenantId)
    {
        _store = store;
        _tenantId = tenantId;
    }

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
        => _store.StorageFor(typeof(T), _tenantId).TryGetValue(id, out var found)
           && found is T
           && !_store.IsDeleted(typeof(T), _store.ScopeFor(typeof(T), _tenantId), id)
            ? InMemoryDocumentStore.Copy<T>(found)
            : default;

    public IQueryable<T> Query<T>() where T : notnull
        => InMemoryDocumentQueryable<T>.Wrap(_store.SnapshotOf<T>(_tenantId).AsQueryable());

    public void Store<T>(params T[] entities) where T : notnull
    {
        foreach (var entity in entities)
        {
            var id = InMemoryDocumentStore.IdentityOf(entity);
            _pending.Add(changes =>
            {
                var storage = _store.StorageFor(typeof(T), _tenantId);

                // Insert vs update is decided at commit time against what is actually stored, which
                // is the only honest answer a store with no identity map can give. The contract
                // deliberately does not hold products to one determination here -- only to the
                // document landing in exactly one of the two collections.
                var existed = storage.ContainsKey(id);

                guardVersion(entity, storage, id, existed);

                // A COPY, not the caller's instance: a real store serializes at commit, so a caller
                // mutating its object afterwards must not rewrite stored state behind the store's back.
                // Taken after guardVersion so the caller's own instance carries the landed version.
                storage[id] = InMemoryDocumentStore.Copy<T>(entity)!;
                _store.RecordWrite(typeof(T), _store.ScopeFor(typeof(T), _tenantId), id, entity.GetType());

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

    /// <summary>
    /// The <see cref="IVersioned" /> Guid optimistic concurrency guard (jasperfx#819), run at commit
    /// time against what is actually stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here because of jasperfx#903, and the reason is worth recording rather than leaving as an
    /// implementation note. The reference store left <c>SupportsOptimisticConcurrency</c> false, so
    /// <c>DocumentConjoinedTenancyCompliance</c>'s Guid concurrency fact <em>skipped</em> in this
    /// repository — and it shipped in 2.75.0 asserting something no correct store could satisfy,
    /// because it re-stored the instance that had just performed the winning write. A fact that skips
    /// everywhere it could be run is a fact nobody has checked.
    /// </para>
    /// <para>
    /// The contradiction it hit is exactly what implementing this makes visible <em>here</em>: the
    /// landed version is written back onto the stored instance, which is mandated by
    /// <c>GuidOptimisticConcurrencyCompliance.a_successful_write_moves_the_instances_own_version_on</c>
    /// so that a long-lived instance stays usable. Only a store enrolled in BOTH suites can catch a
    /// fact in one contradicting a fact in the other.
    /// </para>
    /// <para>
    /// <see cref="Guid.Empty" /> is the "no expectation" sentinel, the same role revision <c>0</c>
    /// plays for numeric revisions: a brand-new instance carries it and is stamped rather than refused.
    /// The five facts of the Guid suite do not pin what an empty version means over an EXISTING row,
    /// so this admits it rather than inventing a refusal the contract does not state.
    /// </para>
    /// </remarks>
    private void guardVersion<T>(
        T entity, ConcurrentDictionary<object, object> storage, object id, bool existed) where T : notnull
    {
        if (!_store.OptimisticConcurrencyTypes.Contains(typeof(T))) return;

        // polecat#720: the marker interface first, then a member the configuration named. Reading the
        // version through one accessor rather than two branches is the point -- the four independent
        // sightings of this bug are all a second code path that forgot to ask.
        var member = _store.MappedVersionMembers.GetValueOrDefault(typeof(T));
        if (entity is not IVersioned && member is null) return;

        var expected = VersionOf(entity, member);

        if (existed && storage[id] is { } raw)
        {
            // A non-empty version is a claim about what the caller believes is stored. If it is wrong,
            // the write is refused and the stored row stands untouched -- asserted by
            // a_stale_instance_is_refused_and_the_winner_stands.
            if (expected != Guid.Empty && expected != VersionOf(raw, member))
            {
                throw new ConcurrencyException(typeof(T), id);
            }
        }

        // The write-back. Mutating the caller's instance is the contract, not a convenience.
        ApplyVersion(entity, member, Guid.NewGuid());
    }

    private static Guid VersionOf(object entity, MemberInfo? member)
        => entity switch
        {
            IVersioned versioned => versioned.Version,
            _ => member switch
            {
                PropertyInfo property => (Guid)(property.GetValue(entity) ?? Guid.Empty),
                FieldInfo field => (Guid)(field.GetValue(entity) ?? Guid.Empty),
                _ => Guid.Empty
            }
        };

    private static void ApplyVersion(object entity, MemberInfo? member, Guid version)
    {
        if (entity is IVersioned versioned)
        {
            versioned.Version = version;
            return;
        }

        switch (member)
        {
            case PropertyInfo property:
                property.SetValue(entity, version);
                break;
            case FieldInfo field:
                field.SetValue(entity, version);
                break;
        }
    }

    public void Delete<T>(T entity) where T : notnull
        => deleteById<T>(InMemoryDocumentStore.IdentityOf(entity));

    public void Delete<T>(Guid id) where T : notnull => deleteById<T>(id);

    public void Delete<T>(string id) where T : notnull => deleteById<T>(id);

    private void deleteById<T>(object id) where T : notnull
        => _pending.Add(changes =>
        {
            _store.Remove(typeof(T), _tenantId, id);
            changes.RecordDeleted(typeof(T), id);
        });

    public void DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
    {
        var matches = expression.Compile();
        _pending.Add(changes =>
        {
            var storage = _store.StorageFor(typeof(T), _tenantId);
            var scope = _store.ScopeFor(typeof(T), _tenantId);
            foreach (var pair in storage.ToArray())
            {
                if (pair.Value is T document && !_store.IsDeleted(typeof(T), scope, pair.Key) && matches(document))
                {
                    _store.Remove(typeof(T), _tenantId, pair.Key);
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
