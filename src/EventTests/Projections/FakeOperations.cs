using JasperFx.Events;
using JasperFx.Events.Daemon;

namespace EventTests.Projections;

public class FakeOperations : FakeSession, IStorageOperations
{
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    // jasperfx#525: set this to have FetchProjectionStorageAsync hand back a stubbed storage (used by the
    // deferred-rebuild flush tests); left null it preserves the original throwing behavior.
    public object? ProjectionStorage { get; set; }

    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken)
    {
        if (ProjectionStorage is IProjectionStorage<TDoc, TId> storage)
        {
            return Task.FromResult(storage);
        }

        throw new NotImplementedException();
    }

    public bool EnableSideEffectsOnInlineProjections { get; set; } = false;

    // jasperfx#1101: left null it preserves the original throwing behavior, the same as ProjectionStorage
    public IMessageSink? MessageSink { get; set; }

    public ValueTask<IMessageSink> GetOrStartMessageSink()
    {
        if (MessageSink != null)
        {
            return new ValueTask<IMessageSink>(MessageSink);
        }

        throw new NotImplementedException();
    }
}

public class FakeSession
{
    
}