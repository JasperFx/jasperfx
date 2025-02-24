#nullable enable
namespace JasperFx.Events.Projections;

public interface IMetadataApplication
{
    object ApplyMetadata(object aggregate, IEvent lastEvent);
}
