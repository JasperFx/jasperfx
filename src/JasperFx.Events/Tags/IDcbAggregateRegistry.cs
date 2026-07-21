using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Tags;

/// <summary>
/// A single discovered DCB aggregate: its CLR type and the tag types that define
/// its slice/consistency-boundary shape. Recorded the first time the aggregate is
/// used inside a DCB operation, since such aggregates have no registered projection
/// name and are not emitted by the source generator when constructed deep in handler
/// or endpoint code.
/// </summary>
/// <param name="AggregateType">The DCB aggregate's CLR type — the token a run target is identified by.</param>
/// <param name="TagTypes">The tag type registrations that shape this aggregate's DCB slice.</param>
public sealed record DcbAggregateRegistration(
    Type AggregateType,
    IReadOnlyList<ITagTypeRegistration> TagTypes);

/// <summary>
/// Runtime registry of DCB aggregates that are only ever used <em>inside</em> a DCB
/// operation — buried in nested handler/endpoint code where the source generator can't
/// see them and where there is no registered named projection to identify them by.
/// Marten/Polecat populate this <b>lazily on first DCB use</b> by instrumenting the DCB
/// read entrypoints (<c>FetchForWritingByTags&lt;T&gt;</c>, <c>AggregateByTagsAsync&lt;T&gt;</c>),
/// so these aggregates become steppable/inspectable run targets identified by <b>type</b>
/// rather than by projection name, and become visible in CritterWatch's lifecycle/descriptor
/// views. See jasperfx#546.
/// </summary>
public interface IDcbAggregateRegistry
{
    /// <summary>
    /// Record a DCB aggregate on first use. Idempotent and safe to call concurrently —
    /// the first registration for a given aggregate type wins and is returned on every
    /// subsequent call, so instrumenting a hot read path costs one dictionary lookup.
    /// </summary>
    /// <param name="aggregateType">The DCB aggregate's CLR type.</param>
    /// <param name="tagTypes">The tag type registrations that shape its slice.</param>
    /// <returns>The stored registration (existing one if already registered).</returns>
    DcbAggregateRegistration Register(Type aggregateType, IReadOnlyList<ITagTypeRegistration> tagTypes);

    /// <summary>
    /// Look up a previously discovered DCB aggregate by its CLR type.
    /// </summary>
    bool TryFind(Type aggregateType, [NotNullWhen(true)] out DcbAggregateRegistration? registration);

    /// <summary>
    /// Every DCB aggregate discovered so far, in an order-independent snapshot.
    /// </summary>
    IReadOnlyCollection<DcbAggregateRegistration> Registrations { get; }
}

/// <summary>
/// Default thread-safe <see cref="IDcbAggregateRegistry"/>. Keyed by aggregate CLR type;
/// registration is idempotent via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
/// so a lazily-instrumented hot DCB read path never double-registers or throws under contention.
/// </summary>
public sealed class DcbAggregateRegistry : IDcbAggregateRegistry
{
    private readonly ConcurrentDictionary<Type, DcbAggregateRegistration> _registrations = new();

    /// <inheritdoc />
    public DcbAggregateRegistration Register(Type aggregateType, IReadOnlyList<ITagTypeRegistration> tagTypes)
    {
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(tagTypes);

        return _registrations.GetOrAdd(aggregateType, static (type, tags) =>
            new DcbAggregateRegistration(type, tags), tagTypes);
    }

    /// <inheritdoc />
    public bool TryFind(Type aggregateType, [NotNullWhen(true)] out DcbAggregateRegistration? registration)
    {
        return _registrations.TryGetValue(aggregateType, out registration);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DcbAggregateRegistration> Registrations => _registrations.Values.ToArray();
}
