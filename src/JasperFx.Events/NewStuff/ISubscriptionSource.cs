using JasperFx.Events.Projections;

namespace JasperFx.Events.NewStuff;

public interface ISubscriptionSource
{
    public AsyncOptions Options { get; }
    // TODO -- might need to make this be async
    IReadOnlyList<IAsyncShard> AsyncProjectionShards();

    public string Name { get; }
    public uint Version { get; }
}