using JasperFx.Descriptors;

namespace JasperFx.Events.Projections.Composite;

public class ProjectionStage<TOperations, TQuerySession>(int Order)
    where TOperations : TQuerySession, IStorageOperations
{
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

    public List<IProjectionSource<TOperations, TQuerySession>> Projections { get; } = [];
}