using JasperFx.Descriptors;

namespace JasperFx.Events.Projections.Composite;

public class ProjectionStage<TOperations, TQuerySession>(int order)
    where TOperations : TQuerySession, IStorageOperations
{
    public int Order { get; } = order;

    private readonly List<IProjectionSource<TOperations, TQuerySession>> _projections = new();

    public OptionsDescription ToDescription(IEventStore store)
    {
        var description = new OptionsDescription(this);
        var @set = description.AddChildSet("Projections");

        foreach (var projection in Projections)
        {
            @set.Rows.Add(projection.Describe(store));
        }

        return description;
    }

    public void Add<T>() where T : IProjectionSource<TOperations, TQuerySession>, new()
    {
        _projections.Add(new T());
    }

    public void Add(IProjectionSource<TOperations, TQuerySession> projection) => _projections.Add(projection);

    public IReadOnlyList<IProjectionSource<TOperations, TQuerySession>> Projections => _projections;
}