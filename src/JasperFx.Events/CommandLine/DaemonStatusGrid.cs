using JasperFx.Blocks;
using JasperFx.Core;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

internal class DaemonStatusGrid
{
    private readonly BatchingChannel<DaemonStatusMessage> _batching;
    private readonly LightweightCache<Uri, StoreDaemonStatus> _stores = new(subject => new StoreDaemonStatus(subject));
    private readonly Table _table;
    private LiveDisplayContext _context = null!;

    public DaemonStatusGrid()
    {
        var updates = new Block<DaemonStatusMessage[]>(UpdateBatch);
        _batching = new BatchingChannel<DaemonStatusMessage>(100.Milliseconds(), updates);

        _table = new Table();

        _table.AddColumn("Projections");
        _table.Columns[0].Alignment = Justify.Center;

        var completion = new TaskCompletionSource();

#pragma warning disable VSTHRD110
        AnsiConsole.Live(_table).StartAsync(ctx =>
#pragma warning restore VSTHRD110
        {
            _context = ctx;

            return completion.Task;
        });
    }

    internal void UpdateBatch(DaemonStatusMessage[] messages)
    {
        foreach (var message in messages)
            _stores[message.SubjectUri].ReadState(message.DatabaseIdentifier, message.State);

        var storeTables = _stores.OrderBy(x => x.Subject).Select(x => x.BuildTable()).ToArray();

        for (var i = 0; i < storeTables.Length; i++)
        {
            var table = storeTables[i];
            if (_table.Rows.Count < i + 1)
            {
                _table.AddRow(table);
            }
            else
            {
                _table.Rows.Update(i, 0, table);
            }
        }

        _context?.Refresh();
    }

    public void Post(DaemonStatusMessage message)
    {
#pragma warning disable VSTHRD110
        _batching.Post(message);
#pragma warning restore VSTHRD110
    }
}