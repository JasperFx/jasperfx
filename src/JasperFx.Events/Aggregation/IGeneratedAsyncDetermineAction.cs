using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Interface for source-generated evolvers that handle ShouldDelete-bearing projections whose
/// Apply/Create/ShouldDelete methods are async and/or require an IQuerySession. This is the
/// async sibling of <see cref="IGeneratedSyncDetermineAction{TDoc,TId}"/>.
///
/// It exists so the partial-projection dispatcher can be emitted as a standalone, file-scoped
/// evolver type (registered via <see cref="GeneratedEvolverAttribute"/>) instead of as
/// overridden members on the user's projection class. Emitting members into the user's class is
/// not safe when the generator is bundled in two referenced packages (e.g. Marten + Polecat) and
/// loads twice -- the duplicate members trip CS0111. See https://github.com/JasperFx/jasperfx/issues/462.
/// </summary>
public interface IGeneratedAsyncDetermineAction<TDoc, TId> where TDoc : notnull where TId : notnull
{
    ValueTask<(TDoc?, ActionType)> DetermineActionAsync(TDoc? snapshot, TId id, IReadOnlyList<IEvent> events,
        object session, CancellationToken cancellation);

    Type[] EventTypes { get; }
}
