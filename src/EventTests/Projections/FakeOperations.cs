using JasperFx.Events;
using JasperFx.Events.Daemon;

namespace EventTests.Projections;

public class FakeOperations : FakeSession, IStorageOperations
{
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public bool EnableSideEffectsOnInlineProjections { get; } = false;
    public ValueTask<IMessageSink> GetOrStartMessageSink()
    {
        throw new NotImplementedException();
    }
}

public class FakeSession
{
    
}