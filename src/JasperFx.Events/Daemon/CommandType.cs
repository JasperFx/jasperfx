namespace JasperFx.Events.Daemon;

internal enum CommandType
{
    Start,
    HighWater,
    RangeCompleted,

    // #4721: posted by the off-consumer optimized-rebuild task when it finishes, so the agent
    // reconciles its in-memory marks and resumes continuous operation on the command-loop thread.
    ReplayCompleted,

    // jasperfx#525: posted by a deferred-rebuild execution after it has buffered (but NOT committed) a
    // range. It advances the buffered ceiling — which drives loading back-pressure — so the daemon keeps
    // pumping the next page, while committed progression (LastCommitted) stays put until the next flush.
    RangeBuffered
}
