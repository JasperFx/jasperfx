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

    // jasperfx#539: AgentStatus string published on the HighWaterMark ShardState while the loop is cycling.
    private const string RunningStatus = "Running";

    // jasperfx#539: UtcTicks of the last completed poll cycle (0 == no cycle yet). This is the loop's
    // liveness heartbeat and is read from the watchdog timer thread, so it is written/read via Interlocked.
    private long _lastPolledAtTicks;

    // jasperfx#539: watchdog remediation bookkeeping. _remediating serializes overlapping restarts (the
    // health timer keeps firing while a restart is in flight); _lastRemediation caps restarts to once per
    // staleness window. Both are only touched inside checkState under the _remediating guard.
    private int _remediating;
    private DateTimeOffset _lastRemediation;

    // jasperfx#539: bumped on every StartAsync. Each poll loop captures its generation and exits the instant
    // it observes a newer one. RestartAsync abandons a wedged loop without awaiting it (awaiting a hung loop
    // would hang the remediation), so this guard is what guarantees an abandoned loop that later recovers
    // can never run beside the fresh loop.
    private int _loopGeneration;

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

        // jasperfx#539: seed the liveness heartbeat so IsStale is meaningful from the first cycle and the
        // Started state carries a heartbeat for consumers that only watch the live Tracker.
        Interlocked.Exchange(ref _lastPolledAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        await _tracker.PublishAsync(
            new ShardState(ShardState.HighWaterMark, _current.CurrentMark)
            {
                Action = ShardAction.Started,
                LastAdvanced = lastAdvancedFrom(_current),
                LastHeartbeat = DateTimeOffset.UtcNow,
                AgentStatus = RunningStatus
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

        // jasperfx#539: stamp this loop's generation so a later RestartAsync can retire it even if it is
        // wedged (see _loopGeneration). Captured into the delegate below so there is no field-read race.
        var generation = Interlocked.Increment(ref _loopGeneration);

        // #524 (defect C): StartNew over an async delegate hands back a Task<Task> that completes as soon as
        // detectChanges hits its first await — the real poll loop is the inner task. Unwrap so _loop *is* that
        // inner task; otherwise _loop.IsFaulted is always false and the checkState watchdog can never see the
        // loop fault, let alone restart it.
        _loop = Task.Factory.StartNew(() => detectChanges(generation), _token,
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

    private async Task detectChanges(int generation)
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
            // jasperfx#539: a superseded loop (RestartAsync bumped the generation) exits before doing any
            // work, so a wedged loop that later recovers can never run beside its replacement.
            if (!IsRunning || Volatile.Read(ref _loopGeneration) != generation)
            {
                break;
            }

            // jasperfx#539: stamp + publish the liveness heartbeat at the top of every cycle. Reaching here
            // is proof the loop is cycling (a wedged wakeup or a hung Detect never returns to this point, so
            // LastPolledAt ages out and the watchdog restarts the loop). The heartbeat carries the CURRENT
            // mark — never a higher one — so it can never force the mark forward.
            await heartbeatAsync().ConfigureAwait(false);

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

    /// <summary>
    /// jasperfx#539: heartbeat of the last completed poll cycle — proof the loop is *cycling*, independent
    /// of whether the high-water mark is *advancing*. Null until the first cycle. Exposed in-memory so a
    /// local health check can read it without the ExtendedProgression columns.
    /// </summary>
    public DateTimeOffset? LastPolledAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPolledAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// jasperfx#539: true when the poll loop has faulted with an unhandled exception.
    /// </summary>
    public bool Faulted => _loop is { IsFaulted: true };

    /// <summary>
    /// jasperfx#539: true when the poll loop has not completed a cycle within <paramref name="threshold"/> —
    /// i.e. it is alive-but-wedged (a stuck connection or a custom <see cref="IDaemonWakeup"/> that stopped
    /// firing). This is measured against heartbeat age, NOT against the mark advancing, so a quiet store with
    /// no new events is never stale. Always false under per-tenant partitioning: there the store-global loop
    /// does not run and liveness is owned by the daemon's tenant high-water path (Path B).
    /// </summary>
    public bool IsStale(TimeSpan threshold, DateTimeOffset now)
    {
        if (!IsRunning || _detector.SupportsTenantPartitioning)
        {
            return false;
        }

        var ticks = Interlocked.Read(ref _lastPolledAtTicks);
        if (ticks == 0)
        {
            // No cycle has completed yet — can't be judged stale.
            return false;
        }

        return now - new DateTimeOffset(ticks, TimeSpan.Zero) > threshold;
    }

    /// <summary>
    /// jasperfx#539: stop then start the poll loop. StartAsync re-reads the real mark via Detect, so this
    /// re-establishes the loop WITHOUT ever advancing the high-water mark. Publishes a <see cref="ShardAction.Restarted"/>
    /// state so consumers can distinguish a remediation from an ordinary advance.
    /// </summary>
    public async Task RestartAsync()
    {
        if (_token.IsCancellationRequested)
        {
            return;
        }

        // Detach the current loop WITHOUT awaiting it. A stale loop is wedged by definition — a hung Detect
        // or a wakeup that stopped firing — so awaiting it, as a graceful StopAsync would, hangs the very
        // remediation meant to recover from that. StartAsync bumps _loopGeneration, so the abandoned loop
        // becomes a no-op the instant it ever resumes and cannot double-run beside the fresh loop.
        _timer.Stop();
        IsRunning = false;

        await StartAsync().ConfigureAwait(false);
        await publishStatusAsync(ShardAction.Restarted, "Restarted").ConfigureAwait(false);
    }

    // jasperfx#539: stamp the heartbeat and publish a Running HighWaterMark state carrying it. The published
    // sequence is never higher than the current mark, so a heartbeat can never move the mark forward.
    private ValueTask heartbeatAsync()
    {
        Interlocked.Exchange(ref _lastPolledAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return publishStatusAsync(ShardAction.Updated, RunningStatus);
    }

    private ValueTask publishStatusAsync(ShardAction action, string status)
    {
        var now = DateTimeOffset.UtcNow;
        var mark = Math.Max(_current?.CurrentMark ?? 0L, _tracker.HighWaterMark);

        return _tracker.PublishAsync(new ShardState(ShardState.HighWaterMark, mark)
        {
            Action = action,
            AgentStatus = status,
            LastHeartbeat = now,
            LastAdvanced = _current == null ? null : lastAdvancedFrom(_current)
        });
    }

    private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        _ = checkState();
    }

    // jasperfx#539: the watchdog now restarts the loop when it has *faulted* OR gone *stale* (stopped
    // completing cycles within HighWaterStalenessThreshold). Restarts are capped to once per threshold window
    // and never overlap. Staleness is only meaningful for Path A — under partitioning the daemon governs the
    // tenant high-water path instead, so this bails out early.
    private async Task checkState()
    {
        if (_token.IsCancellationRequested || _detector.SupportsTenantPartitioning)
        {
            return;
        }

        var faulted = Faulted;
        var now = DateTimeOffset.UtcNow;
        var stale = IsStale(_settings.HighWaterStalenessThreshold, now);

        if (!faulted && !stale)
        {
            return;
        }

        // Don't let overlapping timer ticks pile up concurrent restarts.
        if (Interlocked.CompareExchange(ref _remediating, 1, 0) == 1)
        {
            return;
        }

        try
        {
            // Cap remediation to once per staleness window so a loop that fails to re-establish isn't
            // thrashed by the (much faster) health-check timer.
            if (_lastRemediation != default && now - _lastRemediation < _settings.HighWaterStalenessThreshold)
            {
                return;
            }

            _lastRemediation = now;

            if (faulted)
            {
                _logger.LogWarning(_loop.Exception,
                    "HighWaterAgent polling loop faulted for database {Name}; restarting", _detector.DatabaseUri);
                await publishStatusAsync(ShardAction.Faulted, "Faulted").ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "HighWaterAgent polling loop for database {Name} has not completed a cycle within {Threshold}; restarting",
                    _detector.DatabaseUri, _settings.HighWaterStalenessThreshold);
            }

            await RestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to restart the HighWaterAgent for database {Name}", _detector.DatabaseUri);
        }
        finally
        {
            Volatile.Write(ref _remediating, 0);
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

    /// <summary>
    /// Backstop for the CheckNowAsync catch-up loop. The loop's target is the committed ceiling, which
    /// is reachable by definition, but a detector legitimately holding before an in-flight append (or a
    /// database outage) could stall the loop — after this long CheckNowAsync proceeds with whatever
    /// high water mark it has.
    /// </summary>
    public TimeSpan CheckNowTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task CheckNowAsync()
    {
        // marten#4953: catch up to the highest COMMITTED sequence, never HighWaterStatistics.HighestSequence.
        // That value can be a database sequence's last_value, which includes numbers reserved by
        // transactions still in flight — looping DetectInSafeZone until the mark reached the reserved
        // ceiling pressured the detection into skipping every in-flight gap during concurrent load
        // (a rebuild or catch-up during an import silently lost events) and could spin forever on a
        // rolled-back tail. A detector that holds before in-flight gaps now simply makes this loop
        // wait for those appends to land; the committed ceiling is reachable by definition.
        var ceiling = await _detector.FetchCommittedHighWaterCeilingAsync(_token).ConfigureAwait(false);

        var statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);

        // Get out of here if you're at the initial, empty state
        if (statistics.HighestSequence == 1 && statistics.CurrentMark == 0)
        {
            await _tracker.MarkHighWaterAsync(statistics.CurrentMark, lastAdvancedFrom(statistics));
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        while (statistics.CurrentMark < ceiling && stopwatch.Elapsed < CheckNowTimeout &&
               !_token.IsCancellationRequested)
        {
            await Task.Delay(_settings.SlowPollingTime, _token).ConfigureAwait(false);
            statistics = await _detector.DetectInSafeZone(_token).ConfigureAwait(false);
        }

        if (statistics.CurrentMark < ceiling)
        {
            _logger.LogWarning(
                "CheckNowAsync for database {Name} stopped at high water mark {CurrentMark} before reaching the committed ceiling {Ceiling} (timeout {Timeout} or cancellation). Continuing with the current mark",
                _detector.DatabaseUri, statistics.CurrentMark, ceiling, CheckNowTimeout);
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