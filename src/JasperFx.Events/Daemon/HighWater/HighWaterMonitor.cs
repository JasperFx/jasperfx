using System.Diagnostics.Metrics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
///     Reusable, store-agnostic implementation of <see cref="IHighWaterMonitor" /> that wraps a single
///     <see cref="HighWaterAgent" /> — the same high-water polling loop a full daemon runs — without any projection
///     shards. Concrete event stores (Marten, Polecat) construct one of these from their own
///     <see cref="IHighWaterDetector" /> + <see cref="DaemonSettings" /> to satisfy
///     <see cref="IEventStore.BuildHighWaterMonitorAsync" />, so the standalone-detector contract lives here rather
///     than being re-implemented per store. See <see href="https://github.com/JasperFx/CritterWatch/issues/675" />.
/// </summary>
public class HighWaterMonitor : IHighWaterMonitor
{
    private readonly Meter _meter;
    private readonly IHighWaterDetector _detector;
    private readonly DaemonSettings _settings;
    private readonly ILogger _logger;
    private readonly bool _ownsTracker;

    private HighWaterAgent? _agent;
    private CancellationTokenSource? _cancellation;

    /// <param name="meter">The event store's <see cref="IEventStore.Meter" />, so the standalone loop emits the same skip metric a full daemon does.</param>
    /// <param name="detector">The store's high-water detector for the target database.</param>
    /// <param name="settings">Daemon settings governing polling cadence and stale-gap handling.</param>
    /// <param name="logger">Logger for the agent's diagnostic output.</param>
    /// <param name="tracker">
    ///     Optional tracker to publish high-water progression to. When null the monitor creates and owns its own,
    ///     which is the display-only default: the monitor runs independently of any projection daemon, so it does
    ///     not borrow a daemon's tracker.
    /// </param>
    public HighWaterMonitor(Meter meter, IHighWaterDetector detector, DaemonSettings settings, ILogger logger,
        ShardStateTracker? tracker = null)
    {
        _meter = meter;
        _detector = detector;
        _settings = settings;
        _logger = logger;

        _ownsTracker = tracker == null;
        Tracker = tracker ?? new ShardStateTracker(logger);
    }

    public Uri DatabaseUri => _detector.DatabaseUri;

    public ShardStateTracker Tracker { get; }

    public long CurrentMark => Tracker.HighWaterMark;

    public bool IsRunning => _agent is { IsRunning: true };

    public async Task StartAsync(CancellationToken token)
    {
        if (IsRunning)
        {
            return;
        }

        // Fresh linked source per start so the monitor can be stopped and restarted (e.g. as single-node
        // election moves the detector between nodes).
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _agent = new HighWaterAgent(_meter, _detector, Tracker, _logger, _settings, _cancellation.Token);
        await _agent.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_agent != null)
        {
            await _agent.StopAsync().ConfigureAwait(false);
            _agent.SafeDispose();
            _agent = null;
        }

        if (_cancellation != null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _cancellation.SafeDispose();
            _cancellation = null;
        }
    }

    public Task<HighWaterStatistics> DetectAsync(CancellationToken token)
    {
        return _detector.Detect(token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsTracker)
        {
            (Tracker as IDisposable)?.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
