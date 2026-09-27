using System.Diagnostics;
using JasperFx.Blocks;
using Shouldly;

namespace CoreTests.Blocks;

/// <summary>
/// A block's workers must not inherit the <see cref="Activity" /> that happened to be current when
/// the block was constructed (jasperfx#900).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Block{T}" /> starts its workers with <c>Task.Run</c> in the constructor, and
/// <c>Task.Run</c> flows the <see cref="ExecutionContext" /> — so the workers captured whatever
/// <see cref="Activity.Current" /> was set at construction and kept it for the block's entire
/// lifetime, long after that activity had ended. Every span the block's action started became a child
/// of it.
/// </para>
/// <para>
/// The reported consequence is what makes this more than untidy telemetry. A Marten async daemon
/// whose agents were rebuilt from inside an HTTP handler parented every subsequent projection page
/// span under that one request: 3,943 spans in ten minutes, on a trace that had long since been
/// reported as finished. A block is a long-lived worker pool, so a construction-time ambient value is
/// never the right parent for work posted later.
/// </para>
/// <para>
/// The fix is narrow on purpose. <c>ExecutionContext.SuppressFlow()</c> around the <c>Task.Run</c>
/// calls would also work and was rejected: it stops <em>every</em> <see cref="AsyncLocal{T}" /> from
/// reaching the workers, logging scopes and the current culture included, and a caller relying on
/// those would silently lose them. Clearing only the ambient activity changes nothing else about what
/// a worker inherits.
/// </para>
/// </remarks>
public class BlockActivityIsolationTests : IDisposable
{
    private readonly ActivitySource theSource = new("jasperfx.tests.block-activity");
    private readonly ActivityListener theListener;

    public BlockActivityIsolationTests()
    {
        // Without a listener that samples, StartActivity returns null and every assertion below would
        // pass vacuously -- the bug is about an activity's PARENT, so there has to be one at all.
        theListener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == theSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(theListener);
    }

    public void Dispose()
    {
        theListener.Dispose();
        theSource.Dispose();
    }

    /// <summary>
    /// The jasperfx#900 repro, reduced: construct the block inside an activity, let that activity end,
    /// then post.
    /// </summary>
    [Fact]
    public async Task work_posted_later_does_not_get_parented_under_the_construction_time_activity()
    {
        var completion = new TaskCompletionSource<Activity?>();

        Block<int> block;
        using (var request = theSource.StartActivity("request"))
        {
            // The guard for the guard: if the listener is not sampling, there is no ambient activity to
            // inherit and the fact proves nothing.
            request.ShouldNotBeNull();

            block = new Block<int>((_, _) =>
            {
                using var work = theSource.StartActivity("work");
                completion.TrySetResult(work);
                return Task.CompletedTask;
            });
        }

        block.Post(1);

        var spanned = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        spanned.ShouldNotBeNull();
        spanned.Parent.ShouldBeNull("the block's worker inherited the activity current when the block was constructed");

        await block.DisposeAsync();
    }

    /// <summary>
    /// The ambient activity is cleared per item, not once per worker.
    /// </summary>
    /// <remarks>
    /// An action that starts an activity and does not dispose it leaves it current on that worker's
    /// execution context, so clearing only before the loop would let the first item's leak become the
    /// second item's parent. Per-item is the invariant worth having: every posted item's work is a
    /// trace root regardless of what the item before it left behind.
    /// </remarks>
    [Fact]
    public async Task an_undisposed_activity_from_one_item_does_not_parent_the_next()
    {
        var parents = new List<Activity?>();
        var completion = new TaskCompletionSource();

        // parallelCount 1 so both items land on the same worker -- the only arrangement where one
        // item's leak can reach the next.
        await using var block = new Block<int>(1, (i, _) =>
        {
            // Deliberately NOT disposed, which is the leak being defended against.
            var leaked = theSource.StartActivity($"item-{i}");
            parents.Add(leaked?.Parent);

            if (parents.Count == 2) completion.TrySetResult();

            return Task.CompletedTask;
        });

        block.Post(1);
        block.Post(2);

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        parents.Count.ShouldBe(2);
        parents.ShouldAllBe(x => x == null);
    }

    /// <summary>
    /// An activity the action starts itself is still the parent of what it starts next — clearing the
    /// ambient value must not break ordinary nesting inside one item.
    /// </summary>
    /// <remarks>
    /// The control for the two facts above. A fix that reached too far — clearing the activity around
    /// each <c>await</c>, or suppressing flow so the action could not establish a scope at all — would
    /// pass both of them and make the block useless for tracing its own work.
    /// </remarks>
    [Fact]
    public async Task an_activity_started_inside_the_action_still_parents_its_own_children()
    {
        var completion = new TaskCompletionSource<string?>();

        await using var block = new Block<int>((_, _) =>
        {
            using var outer = theSource.StartActivity("outer");
            using var inner = theSource.StartActivity("inner");

            completion.TrySetResult(inner?.Parent?.OperationName);
            return Task.CompletedTask;
        });

        block.Post(1);

        var parentName = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        parentName.ShouldBe("outer");
    }

    /// <summary>
    /// Clearing the worker's ambient activity does not reach back into the caller's.
    /// </summary>
    /// <remarks>
    /// <see cref="AsyncLocal{T}" /> writes after a <c>Task.Run</c> boundary do not propagate to the
    /// caller, so this holds by construction — asserted because the fix is a write to ambient state
    /// and "it only affects the worker" is the claim the whole approach rests on.
    /// </remarks>
    [Fact]
    public async Task the_callers_own_activity_survives()
    {
        var completion = new TaskCompletionSource();

        using var request = theSource.StartActivity("request");
        request.ShouldNotBeNull();

        await using var block = new Block<int>((_, _) =>
        {
            completion.TrySetResult();
            return Task.CompletedTask;
        });

        block.Post(1);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Activity.Current.ShouldBeSameAs(request);
    }
}
