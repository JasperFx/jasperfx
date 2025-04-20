using JasperFx.Events.Projections;

namespace JasperFx.Events.CommandLine;

internal class DaemonWatcher : IObserver<ShardState>
{
    private readonly Uri _subjectUri;
    private readonly string _databaseName;
    private readonly DaemonStatusGrid _grid;

    public DaemonWatcher(Uri subjectUri, string databaseName, DaemonStatusGrid grid)
    {
        _subjectUri = subjectUri;
        _databaseName = databaseName;
        _grid = grid;
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(ShardState value)
    {
        _grid.Post(new DaemonStatusMessage(_subjectUri, _databaseName, value));
    }
}