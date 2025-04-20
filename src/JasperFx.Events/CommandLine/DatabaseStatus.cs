using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.CommandLine;

internal class DatabaseStatus(string name)
{
    public string Name { get; } = name;
    public long HighWaterMark { get; set; }

    public readonly LightweightCache<string, CurrentShardState> Shards 
        = new(name => new CurrentShardState(name));


    public void ReadState(ShardState state)
    {
        if (state.ShardName == ShardState.HighWaterMark)
        {
            HighWaterMark = state.Sequence;
        }
        else
        {
            Shards[state.ShardName].ReadState(state);
        }
    }
}
