#nullable enable
namespace JasperFx.Events.Projections.Aggregation;

public interface IMetadataApplication
{
    object ApplyMetadata(object aggregate, IEvent lastEvent);
}
