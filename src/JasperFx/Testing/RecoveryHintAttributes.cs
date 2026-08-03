namespace JasperFx.Testing;

/// <summary>
/// Declares that a named class of failure, on the tests in scope, recovers a particular way.
/// </summary>
/// <remarks>
/// <para>
/// A tag or trait says <em>this test</em> is unreliable. A hint says <em>which failure</em> is
/// unreliable and what fixes it — so an assertion failure on the same test is still reported as
/// the bug it is, rather than being retried away with everything else.
/// </para>
/// <para>
/// These attributes are pure declarations. They carry no behaviour and start nothing: a test
/// runner that understands them reads them and decides, and a runner that does not is unaffected.
/// That is why they live here rather than in a runner — a suite already referencing JasperFx,
/// directly or through Marten, Wolverine or Polecat, can write down what it knows about its own
/// flakiness without taking a dependency on whatever ends up running it.
/// </para>
/// <para>
/// <strong>A hint is not permission to retry.</strong> How much time a run may spend is the
/// operator's decision, expressed by whatever budget the runner exposes; what recovers is the
/// author's knowledge, expressed here. A runner honouring these must not let a hint widen its own
/// ceiling, or a test author could escape a limit set by whoever runs the suite.
/// </para>
/// <para>
/// Applicable to a class (every test it owns), a method, or a whole assembly. A runner should
/// treat the narrowest declaration as the winner, so an assembly-wide default can be overridden
/// per class without either knowing about the other.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly,
    AllowMultiple = true)]
public abstract class RecoveryHintAttribute : Attribute
{
    protected RecoveryHintAttribute(Type failureType) => FailureType = failureType;

    /// <summary>The exception type this hint describes. Base types match derived failures.</summary>
    public Type FailureType { get; }

    /// <summary>
    /// Why the author believes this. Intended to reach the run report verbatim, so it should read
    /// as an explanation to whoever is looking at the retry six months from now.
    /// </summary>
    public string? Because { get; set; }

    /// <summary>What to do about it.</summary>
    public abstract DispositionKind Kind { get; }

    /// <summary>
    /// Resources to recycle. Only meaningful for <see cref="ClearsOnRecycleAttribute"/>.
    /// </summary>
    public virtual IReadOnlyList<string> Resources => [];
}

/// <summary>This failure clears by running the test again in the same process.</summary>
/// <example><c>[ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]</c></example>
public sealed class ClearsOnRetryAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryInProcess;
}

/// <summary>
/// This failure clears only in a brand-new process, with the test running alone — the shape of
/// leak that a scope reset cannot undo, like a static cached the first time anything touched it.
/// </summary>
public sealed class ClearsInFreshProcessAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryInFreshProcess;
}

/// <summary>
/// This failure clears only after the named resources are thrown away and stood up fresh.
/// </summary>
/// <example><c>[ClearsOnRecycle("rabbit", typeof(BrokerUnavailableException))]</c></example>
/// <remarks>
/// <paramref name="resources"/> is comma-separated, matching the <c>recycle(rabbit,kafka)</c> tag
/// vocabulary it is meant to share.
/// </remarks>
public sealed class ClearsOnRecycleAttribute(string resources, Type failureType)
    : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryAfterRecycle;

    public override IReadOnlyList<string> Resources { get; } =
        string.IsNullOrWhiteSpace(resources)
            ? []
            : resources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// This failure never clears, so do not spend attempts on it.
/// </summary>
/// <remarks>
/// The counterweight to the rest of the file, and the reason the set is usable at all. Without it,
/// the only way to stop a broad "retry everything three times" policy from re-running a
/// deterministic bug is to take the retry off the test — which also stops the retries that were
/// pulling their weight.
/// </remarks>
public sealed class NeverRecoversAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.FailAndContinue;
}
