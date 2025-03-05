using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

public interface IAsyncShard // might have a subclass for projections
{
    // Assuming that DocumentStore et al will be embedded into this
    
    AsyncOptions Options { get; }
    ShardRole Role { get; }
    Task<ISubscriptionExecution> BuildExecutionAsync(IEventDatabase database, ILoggerFactory loggerFactory,
        CancellationToken cancellation);
    ShardName Name { get; }
    IEventLoader BuildEventLoader(IEventDatabase database, ILoggerFactory loggerFactory);
}