namespace JasperFx.Events.Daemon;

internal enum CommandType
{
    Start,
    HighWater,
    RangeCompleted,

    // #4721: posted by the off-consumer optimized-rebuild task when it finishes, so the agent
    // reconciles its in-memory marks and resumes continuous operation on the command-loop thread.
    ReplayCompleted
}
