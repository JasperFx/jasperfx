using JasperFx.Core;
using JasperFx.Events.Projections;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

public class RebuildWatcher : IObserver<ShardState>
{
    private readonly Cache<string, ProgressTask> _shards
        = new();

    private ProgressContext _context = null!;
    private readonly TaskCompletionSource _completion;

    public RebuildWatcher(long highWaterMark)
    {
        _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _shards.OnMissing = shardName => _context.AddTask(shardName, new ProgressTaskSettings
        {
            AutoStart = true,
            MaxValue = highWaterMark
        });
    }

    public Task Start()
    {
        return AnsiConsole.Progress()
            .AutoRefresh(false)
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(), // Task description
                new ProgressBarColumn(), // Progress bar
                new PercentageColumn(), // Percentage
                new SpinnerColumn() // Spinner
            })
            .StartAsync(c =>
            {
                _context = c;

                return _completion.Task;
            });
    }

    public void Stop()
    {
        _completion.SetResult();
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(ShardState value)
    {
        if (value.ShardName == "HighWaterMark") return;

        var task = _shards[value.ShardName];
        var increment = value.Sequence - task.Value;
        task.Increment(increment);

        _context.Refresh();
    }

}
