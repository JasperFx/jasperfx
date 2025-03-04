#nullable enable
namespace JasperFx.Events.Projections;

[Obsolete("Merge this into IEventDatabase")]
public interface IProjectionStorage
{

}

// NOTES -- maybe just have this implemented by ProjectionDocumentSession as is

public interface IProjectionStorageSession
{
    void DeleteForType(Type documentType);
    void DeleteNamedResource(string resourceName);
}
