using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Daemon;

public class BatchWriteThrottleExtensionsTests
{
    [Fact]
    public void safe_release_swallows_object_disposed_from_a_disposed_throttle()
    {
        // jasperfx#557: the daemon can dispose the shared batch-write throttle while an in-flight batch is
        // still unwinding to its finally. The release must be a no-op then, not an ObjectDisposedException.
        var throttle = new SemaphoreSlim(1);
        throttle.Dispose();

        Should.NotThrow(() => throttle.SafeRelease());
    }

    [Fact]
    public void safe_release_is_a_no_op_on_null()
    {
        SemaphoreSlim? throttle = null;
        Should.NotThrow(() => throttle.SafeRelease());
    }

    [Fact]
    public void safe_release_actually_releases_a_live_throttle()
    {
        using var throttle = new SemaphoreSlim(0, 1);
        throttle.SafeRelease();
        throttle.CurrentCount.ShouldBe(1);
    }
}
