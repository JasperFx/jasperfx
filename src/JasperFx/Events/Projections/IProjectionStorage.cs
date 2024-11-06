namespace JasperFx.Events.Projections;

public interface IProjectionStorageSession
{
    void DeleteForType(Type documentType);
    void DeleteNamedResource(string resourceName);
}