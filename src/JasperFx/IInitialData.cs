namespace JasperFx;

/// <summary>
/// A set of initial data used to pre-populate a document store at startup. Implementers are
/// responsible for not duplicating data across restarts.
/// </summary>
/// <typeparam name="TStore">
/// The store type to populate (e.g. Marten's <c>IDocumentStore</c>, Polecat's <c>IDocumentStore</c>).
/// </typeparam>
/// <remarks>
/// Lifted from the structurally-identical <c>IInitialData</c> in Marten (<c>Marten.Schema</c>)
/// and Polecat — both <c>Task Populate(IDocumentStore, CancellationToken)</c>, blocked from
/// sharing only by the store-typed parameter, resolved here with the generic
/// <typeparamref name="TStore"/>. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public interface IInitialData<in TStore>
{
    /// <summary>
    /// Apply the data loading against the given store.
    /// </summary>
    Task Populate(TStore store, CancellationToken cancellation);
}

/// <summary>
/// A collection of <see cref="IInitialData{TStore}"/> instances executed on startup. Carries
/// Polecat's lambda-overload convenience (which Marten lacked) so a simple populator can be
/// registered without a class.
/// </summary>
public class InitialDataCollection<TStore> : List<IInitialData<TStore>>
{
    /// <summary>
    /// Add a simple lambda-based initial-data populator.
    /// </summary>
    public void Add(Func<TStore, CancellationToken, Task> populate)
    {
        Add(new LambdaInitialData(populate));
    }

    private sealed class LambdaInitialData : IInitialData<TStore>
    {
        private readonly Func<TStore, CancellationToken, Task> _populate;

        public LambdaInitialData(Func<TStore, CancellationToken, Task> populate)
        {
            _populate = populate;
        }

        public Task Populate(TStore store, CancellationToken cancellation)
        {
            return _populate(store, cancellation);
        }
    }
}
