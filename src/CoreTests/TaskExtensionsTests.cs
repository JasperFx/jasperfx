using JasperFx.Core;
using Shouldly;

namespace CoreTests;

public class TaskExtensionsTests
{
    [Fact]
    public async Task completes_when_the_subject_task_completes_in_time()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapped = ((Task)tcs.Task).TimeoutAfterAsync(30_000);

        tcs.SetResult();

        await wrapped;
    }

    [Fact]
    public async Task times_out_when_the_subject_task_never_completes()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Should.ThrowAsync<TimeoutException>(((Task)tcs.Task).TimeoutAfterAsync(50));
    }

    [Fact]
    public async Task propagates_the_subject_tasks_fault()
    {
        // The non-generic overload converted the subject with ContinueWith(_ => true), which runs on
        // ANY completion — so a faulted subject became a successful Task<bool> and the exception was
        // silently swallowed. The daemon relies on this propagating: a rebuild failure faults the
        // agent's rebuild TaskCompletionSource, and ReplayAsync awaits it through this method.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapped = ((Task)tcs.Task).TimeoutAfterAsync(30_000);

        tcs.SetException(new DivideByZeroException("the rebuild failed"));

        var thrown = await Should.ThrowAsync<DivideByZeroException>(wrapped);
        thrown.Message.ShouldBe("the rebuild failed");
    }

    [Fact]
    public async Task propagates_the_subject_tasks_fault_when_already_faulted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetException(new DivideByZeroException("already faulted"));

        await Should.ThrowAsync<DivideByZeroException>(((Task)tcs.Task).TimeoutAfterAsync(30_000));
    }

    [Fact]
    public async Task propagates_the_subject_tasks_cancellation()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapped = ((Task)tcs.Task).TimeoutAfterAsync(30_000);

        tcs.SetCanceled();

        await Should.ThrowAsync<TaskCanceledException>(wrapped);
    }
}
