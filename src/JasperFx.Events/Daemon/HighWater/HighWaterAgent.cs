using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Timers;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace JasperFx.Events.Daemon.HighWater;

public class HighWaterAgent: IDisposable
{
    private readonly IHighWaterDetector _detector;
    private readonly ILogger _logger;
    private readonly DaemonSettings _settings;
    private readonly Timer _timer;
    private readonly ShardStateTracker _tracker;
    private readonly IDaemonWakeup _daemonWakeup;

    private HighWaterStatistics? _current;
    private Task _loop = null!;
    private readonly CancellationToken _token;
    private readonly string _spanName;
    private readonly Counter<int> _skipping;

    // ReSharper disable once ContextualLoggerProblem
    public HighWaterAgent(Meter meter, IHighWaterDetector detector, ShardStateTracker tracker,
        ILogger logger,
        DaemonSettings settings, CancellationToken token)
    {
        _detector = detector;
        _tracker = tracker;
        _logger = logger;
        _settings = settings;
        _token = token;
        _daemonWakeup = settings.Wakeup ?? new TaskDelayDaemonWakeup();

        _timer = new Timer(_settings.HealthCheckPollingTime.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += TimerOnElapsed;

        _spanName = $"{_settings.OtelPrefix}.daemon.highwatermark";

        _skipping = meter.CreateCounter<int>($"{_settings.OtelPrefix}.daemon.skipping");
    }

    public bool IsRunning { get; private set; }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _loop?.SafeDispose();
    }

    public async Task StartAsync()
    {
        IsRunning = true;

        _current = await _detector.Detect(_token).ConfigureAwait(false);

        await _tracker.PublishAsync(
            new ShardState(ShardState.HighWaterMark, _current.CurrentMark)
            {
                Action = ShardAction.Started, LastAdvanced = lastAdvancedFrom(_current)
            });

        // #4913: under per-tenant event partitioning the store-global high-water mark is not used to
        // advance any projection — tenant agents advance from the per-tenant vectorized poll driven by
        // the daemon's TenantedHighWaterCoordinator. The store-global Detect() is `max(seq_id)` fanned
        // out across every tenant partition, so polling it on the recurring loop is pure overhead whose
        // cost scales with the partition count. The mark is seeded once above (so the fallback ceiling and
        // Tracker stay meaningful) and CheckNowAsync still runs an on-demand scan for rebuild/catch-up,
        // but the continuous scan loop is skipped. Per-tenant polling cadence is carried by the daemon's
        // tenant high-water timer instead.
        if (_detector.SupportsTenantPartitioning)
        {
            _logger.LogInformation(
                "Skipping the recurring store-global high water polling loop for database {Name} because per-tenant event partitioning drives high water per tenant",
                _detector.DatabaseUri);
            return;
        }

        // #524 (defect C): StartNew over an async delegate hands back a Task<Task> that completes as soon as
        // detectChanges hits its first await — the real poll loop is the inner task. Unwrap so _loop *is* that
        // inner task; otherwise _loop.IsFaulted is always false and the checkState watchdog can never see the
        // loop fault, let alone restart it.
        _loop = Task.Factory.StartNew(detectChanges, _token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent, TaskScheduler.Default).Unwrap();

        _timer.Start();

        _logger.LogInformation("Started HighWaterAgent for database {Name}", _detector.DatabaseUri);
    }

    // #524 (defect A): the IDaemonWakeup implementation is untrusted. A custom wakeup — e.g. Marten's
    // LISTEN/NOTIFY wakeup, which reconnects a dropped LISTEN connection — can throw a connection error when
    // the database is down or failing over. Such a throw must never escape the poll loop and permanently kill
    // high-water progress, so log it and fall back to a plain delay before the loop continues. Cancellation is
    // the expected shutdown path and is swallowed quietly.
    private async Task waitAsync(TimeSpan delayTime)
    {
        try
        {
            await _daemonWakeup.WaitAsync(delayTime, _token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (_token.IsCancellationRequested)
            {
                return;
            }

            _logger.LogError(e,
                "Error while waiting on the daemon wakeup for database {Name}; falling back to a delay",
                _detector.DatabaseUri);

            try
            {
                await Task.Delay(delayTime, _token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task detectChanges()
    {
        if (!IsRunning)
        {
            return;
        }

        if (_token.IsCancellationRequested) return;

        try
        {
            var next = await _detector.Detect(_token).ConfigureAwait(false);
            if (_current == null || next.CurrentMark > _current.CurrentMark)
            {
                _current = next;
                await _tracker.MarkHighWaterAsync(_current.CurrentMark, lastAdvancedFrom(_current));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed while making the initial determination of the high water mark for database {Name}", _detector.DatabaseUri);
        }

        await waitAsync(_settings.FastPollingTime).ConfigureAwait(false);

        while (!_token.IsCancellationRequested)
        {
            if (!IsRunning)
            {
                break;
            }

            using var activity = _settings.ActivitySource?.StartActivity(_spanName);
            activity?.AddTag(OtelConstants.DatabaseUri, _detector.DatabaseUri);

            HighWaterStatistics? statistics = null;
            try
            {
                statistics = await _detector.Detect(_token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                if (ex.ObjectName.EqualsIgnoreCase("Npgsql.PoolingDataSource"))
                {
                    return;
                }

                _logger.LogError(ex, "Failed while trying to detect high water statistics for database {Name}", _detector.DatabaseUri);
                await waitAsync(_settings.SlowPollingTime).ConfigureAwait(false);

                activity?.AddException(ex);

                continue;

            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed while trying to detect high water statistics for database {Name}", _detector.DatabaseUri);
                activity?.AddException(e);
                await waitAsync(_settings.SlowPollingTime).ConfigureAwait(false);
                continue;
            }

            var status = tagActivity(statistics, activity);

            switch (status)
            {
                case HighWaterStatus.Changed:
                    await markProgressAsync(statistics, _settings.FastPollingTime, status).ConfigureAwait(false);
                    break;

                case HighWaterStatus.CaughtUp:
                    await markProgressAsync(statistics, _settings.SlowPollingTime, status).ConfigureAwait(false);
                    break;

                case HighWaterStatus.Stale:
                    _logger.LogInformation("High Water agent is stale at {CurrentMark} for database {Name}", statistics.CurrentMark, _detector.DatabaseUri);

                    // This gives the high water detection a chance to allow the gaps to fill in
                    // before skipping to the safe harbor time
                    var safeHarborTime = _current!.Timestamp.Add(_settings.StaleSequenceThreshold);
                    if (safeHarborTime > statistics.Timestamp)
                    {
                        await waitAsync(_settings.SlowPollingTime).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogInformation(
                        "High Water agent is stale after threshold of {DelayInSeconds} seconds, skipping gap to events marked after {SafeHarborTime} for database {Name}",
                        _settings.StaleSequenceThreshold.TotalSeconds, safeHarborTime, _detector.DatabaseUri);

                    activity?.SetTag("skipped", "true");

                    var lastKnown = statistics.CurrentMark;

                    statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);

                    status = tagActivity(statistics, activity);
                    activity?.SetTag("last.mark", lastKnown);

                    if (statistics.IncludesSkipping)
                    {
                        await _tracker.MarkSkippingAsync(lastKnown, statistics.CurrentMark, lastAdvancedFrom(statistics));
                    }
                    
                    _skipping.Add(1);

                    await markProgressAsync(statistics, _settings.FastPollingTime, status).ConfigureAwait(false);
                    break;
            }
        }

        _logger.LogInformation("HighWaterAgent has detected a cancellation and has stopped polling for database {Name}", _detector.DatabaseUri);
    }

    private HighWaterStatus tagActivity(HighWaterStatistics statistics, Activity? activity)
    {
        var status = statistics.InterpretStatus(_current!);

        activity?.AddTag("sequence", statistics.HighestSequence);
        activity?.AddTag("status", status.ToString());
        activity?.AddTag("current.mark", statistics.CurrentMark);
        return status;
    }

    private async Task markProgressAsync(HighWaterStatistics statistics, TimeSpan delayTime, HighWaterStatus status)
    {
        if (!IsRunning)
        {
            return;
        }

        // don't bother sending updates if the current position is 0
        if (statistics.CurrentMark == 0 || statistics.CurrentMark == _tracker.HighWaterMark)
        {
            // No matter what, if the status isn't stale, use the current statistics
            if (status != HighWaterStatus.Stale)
            {
                // Update the current stats if the status is not stale
                // This ensures the current stats timestamp is up-to-date, and not just set to the time of the last changed
                // Without this, the StaleSequenceThreshold gets applied to the timestamp of the last changed highwatermark
                // meaning that a time break in events larger than the StaleSequeceThreshold makes the safeHarbourTime less that the timestamp of the processing statistics
                _current = statistics;
            }

            await waitAsync(delayTime).ConfigureAwait(false);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("High Water mark detected at {CurrentMark} for database {Name}", statistics.CurrentMark, _detector.DatabaseUri);
        }

        _current = statistics;

        await _tracker.MarkHighWaterAsync(statistics.CurrentMark, lastAdvancedFrom(statistics));

        await waitAsync(delayTime).ConfigureAwait(false);
    }

    // Surface "when the current mark was observed" (jasperfx#449) so a monitor can compute
    // seconds-since-advance server-side. A default(DateTimeOffset) means the detector didn't
    // supply a timestamp, so publish null rather than a misleading 0001-01-01.
    private static DateTimeOffset? lastAdvancedFrom(HighWaterStatistics statistics)
        => statistics.Timestamp == default ? null : statistics.Timestamp;

    private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        _ = checkState();
    }

    private async Task checkState()
    {
        if (_loop.IsFaulted && !_token.IsCancellationRequested)
        {
            _logger.LogError(_loop.Exception, "HighWaterAgent polling loop was faulted for database {Name}", _detector.DatabaseUri);

            try
            {
                _loop.Dispose();
                await StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error trying to restart the HighWaterAgent for database {Name}", _detector.DatabaseUri);
            }
        }
    }

    /// <summary>
    /// Optimized for the quickest possible check of the high water mark for testing
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public Task<HighWaterStatistics> DetermineCurrentMarkAsync(CancellationToken cancellation)
    {
        return _detector.Detect(cancellation);
    }

    public async Task CheckNowAsync()
    {
        var statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);
        var initialHighMark = statistics.HighestSequence;

        // Get out of here if you're at the initial, empty state
        if (initialHighMark == 1 && statistics.CurrentMark == 0)
        {
            await _tracker.MarkHighWaterAsync(statistics.CurrentMark, lastAdvancedFrom(statistics));
            return;
        }

        while (statistics.CurrentMark < initialHighMark)
        {
            await Task.Delay(_settings.SlowPollingTime, _token).ConfigureAwait(false);
            statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);
        }

        await _tracker.MarkHighWaterAsync(statistics.CurrentMark, lastAdvancedFrom(statistics));
    }

    public async Task StopAsync()
    {
        try
        {
            _timer?.Stop();

            // #524 (defect C): now that _loop is the unwrapped inner poll task, awaiting it here genuinely
            // blocks until the loop exits. The loop only breaks when it sees cancellation or IsRunning == false,
            // so IsRunning must be cleared *before* the await — otherwise a loop parked in a wakeup wakes, never
            // sees the stop signal, and StopAsync hangs forever on a stop that did not cancel the token.
            IsRunning = false;

            if (_loop != null)
            {
                await _loop.ConfigureAwait(false);
                _loop?.Dispose();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to stop the HighWaterAgent for database {Name}", _detector.DatabaseUri);
        }
    }
}