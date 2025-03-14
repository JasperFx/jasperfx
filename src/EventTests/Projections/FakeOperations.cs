using JasperFx.Events;
using JasperFx.Events.Daemon;

namespace EventTests.Projections;

public class FakeOperations : FakeSession, IStorageOperations
{
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IProjectionStorage<TDoc, TId> ProjectionStorageFor<TDoc, TId>(string tenantId)
    {
        throw new NotImplementedException();
    }

    public IProjectionStorage<TDoc, TId> ProjectionStorageFor<TDoc, TId>()
    {
        throw new NotImplementedException();
    }
}

public class FakeSession
{
    
}