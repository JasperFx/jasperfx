using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.NewStuff;

public interface IAsyncShard<TDatabase> // might have a subclass for projections
{
    // Assuming that DocumentStore et al will be embedded into this
    
    AsyncOptions Options { get; }
    ShardRole Role { get; }
    ISubscriptionExecution BuildExecution(TDatabase database, ILoggerFactory loggerFactory);
    ShardName Name { get; }
    IEventLoader BuildEventLoader(TDatabase database, ILoggerFactory loggerFactory);
}