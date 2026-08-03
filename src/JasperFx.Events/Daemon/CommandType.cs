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
    RangeBuffered,

    // jasperfx#598/#610: posted by the off-consumer waiter once this agent has been granted a slot on the
    // daemon's side-effect-gate warm-up throttle. The wait CANNOT happen on the command loop -- that would
    // wedge the agent's high-water and completion bookkeeping for the whole queue time -- so the grant
    // arrives as a command and the loop resumes loading from there.
    WarmupSlotGranted
}
