namespace JasperFx.Testing;

/// <summary>
/// What a test runner should do about an attempt. The vocabulary a
/// <see cref="RecoveryHintAttribute"/> declares against.
/// </summary>
/// <remarks>
/// <para>
/// This lives in JasperFx rather than in a particular test runner so that a test project can
/// declare what it knows about its own failures without taking a dependency on the runner. Any
/// suite already referencing JasperFx — directly, or through Marten, Wolverine or Polecat — can
/// annotate itself and have a runner that understands these attributes act on them.
/// </para>
/// <para>
/// One enum rather than a runner-side copy mapped at a seam: two enums meaning the same thing is
/// how a vocabulary starts drifting.
/// </para>
/// </remarks>
public enum DispositionKind
{
    /// <summary>The attempt succeeded.</summary>
    Pass,

    /// <summary>An ordinary failure: record it and keep going.</summary>
    FailAndContinue,

    /// <summary>Try again in the same process, after resources are reset.</summary>
    RetryInProcess,

    /// <summary>
    /// Try again in a brand-new process, with this test running alone. For the tests that only
    /// pass when nothing else shares their process.
    /// </summary>
    RetryInFreshProcess,

    /// <summary>
    /// Throw the named resources away, stand fresh ones up, then try again. For brokers whose
    /// in-flight state cannot be reliably drained the way a database is truncated.
    /// </summary>
    RetryAfterRecycle,

    /// <summary>Stop the run now — nothing downstream can pass.</summary>
    AbortRun
}
