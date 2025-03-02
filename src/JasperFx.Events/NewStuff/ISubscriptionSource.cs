using JasperFx.Events.Daemon;

namespace JasperFx.Events.NewStuff;

public interface ISubscriptionSource<TStore, TDatabase>
{
    public AsyncOptions Options { get; }
    // TODO -- might need to make this be async
    IReadOnlyList<IAsyncShard<TDatabase>> AsyncProjectionShards();

    public string Name { get; }
    public uint Version { get; }
}