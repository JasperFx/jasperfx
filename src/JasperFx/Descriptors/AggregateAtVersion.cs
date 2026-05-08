using System.Text.Json;

namespace JasperFx.Descriptors;

/// <summary>
/// Strong-typed result of rehydrating an aggregate to a specific stream
/// version. Returned by the event store explorer's "rewind aggregate"
/// view so operators can inspect what an aggregate's state looked like
/// at any point in its history.
/// </summary>
/// <typeparam name="TAggregate">CLR type of the aggregate.</typeparam>
/// <param name="State">Aggregate state at the requested version, or <see langword="null"/> when no events had been applied yet.</param>
/// <param name="Version">Stream version to which the aggregate was rehydrated.</param>
/// <param name="EventsApplied">Number of events that were folded into <paramref name="State"/> to reach this version.</param>
public sealed record AggregateAtVersion<TAggregate>(
    TAggregate? State,
    long Version,
    long EventsApplied);

/// <summary>
/// Untyped variant of <see cref="AggregateAtVersion{TAggregate}"/> used
/// when the aggregate type is referenced by name rather than CLR type.
/// The aggregate state is carried as JSON so monitoring tools can render
/// it without owning the source type.
/// </summary>
/// <param name="TypeName">FullName of the aggregate's CLR type, as known to the event store.</param>
/// <param name="State">Aggregate state at the requested version, serialized as JSON.</param>
/// <param name="Version">Stream version to which the aggregate was rehydrated.</param>
/// <param name="EventsApplied">Number of events that were folded into <paramref name="State"/> to reach this version.</param>
public sealed record AggregateAtVersion(
    string TypeName,
    JsonElement State,
    long Version,
    long EventsApplied);
