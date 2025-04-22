using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.CommandLine;

internal class CurrentShardState
{
    public string ShardName { get; }

    public CurrentShardState(string shardName)
    {
        ShardName = shardName;
    }

    public ShardExecutionState State { get; set; } = ShardExecutionState.Running;

    public long Sequence { get; set; }

    public void ReadState(ShardState state)
    {
        switch (state.Action)
        {
            case ShardAction.Paused:
                State = ShardExecutionState.Paused;
                break;

            case ShardAction.Started:
                Sequence = state.Sequence;
                State = ShardExecutionState.Running;
                break;

            case ShardAction.Stopped:
                State = ShardExecutionState.Stopped;
                break;

            case ShardAction.Updated:
                State = ShardExecutionState.Running;
                Sequence = state.Sequence;
                break;
        }
    }
}