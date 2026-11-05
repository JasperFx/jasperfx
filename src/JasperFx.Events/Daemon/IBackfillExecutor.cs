using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

/// <param name="BatchSize">The shard's own batch size, which for a composite member is the composite's.</param>
public record BackfillRequest(ErrorHandlingOptions ErrorHandling, IDaemonRuntime Runtime, int BatchSize)
{
    /// <summary>
    ///     Receives the backfill's progress, which the agent publishes on its <see cref="ShardState" />.
    /// </summary>
    public Func<BackfillProgress, Task>? Report { get; init; }

    public Task ReportAsync(BackfillProgress progress) => Report?.Invoke(progress) ?? Task.CompletedTask;
}

/// <summary>
///     Where one backfill of a shard stands. <see cref="Ceiling" /> is the live position it is following.
/// </summary>
public record BackfillProgress(string Name, long Position, long Ceiling)
{
    public bool Completed { get; init; }

    /// <summary>
    ///     Why the backfill stopped. The shard keeps running and the backfill resumes on its next start.
    /// </summary>
    public ShardFailure? Failure { get; init; }
}

/// <summary>
///     Fills members that were added to a projected document without a version bump, while the shard keeps
///     projecting. The backfill owns those members until it hands them over: it folds its events up to the
///     shard's live position into storage the live projection never writes, and the agent holds its loading
///     only for <see cref="HandOverAsync" />.
/// </summary>
public interface IBackfillExecutor
{
    /// <summary>
    ///     The projection starts from nothing, so its own Apply produces the members: record every backfill as done.
    /// </summary>
    Task SettleAsync(CancellationToken token);

    /// <summary>
    ///     Fold events alongside the running shard until the backfill is within one batch of
    ///     <paramref name="liveProgression" />. Returns false when there is nothing left to hand over.
    /// </summary>
    Task<bool> CatchUpAsync(Func<long> liveProgression, BackfillRequest request, CancellationToken token);

    /// <summary>
    ///     With the shard held at <paramref name="ceiling" /> and nothing in flight: fold the last events and
    ///     hand the members over to the documents.
    /// </summary>
    Task HandOverAsync(long ceiling, BackfillRequest request, CancellationToken token);
}

/// <summary>
///     A projection source that declares backfills. Implemented by the concrete store, which owns the
///     backfill state and knows how to write its documents.
/// </summary>
public interface IProjectionBackfillSource<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    bool HasBackfills { get; }

    bool TryBuildBackfillExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        ShardName shardName, ILogger logger, [NotNullWhen(true)] out IBackfillExecutor? executor);
}

/// <summary>
///     A grouped projection runner whose projection may declare backfills.
/// </summary>
public interface IBackfillingRunner
{
    bool TryBuildBackfillExecutor(ShardName shardName, [NotNullWhen(true)] out IBackfillExecutor? executor);

    /// <summary>
    ///     Drop cached aggregates, because a hand-over rewrote the documents behind them.
    /// </summary>
    void ResetAggregateCaches();
}

/// <summary>
///     Runs the backfills of the projections that share one shard (the members of a composite), so the shard
///     is held once for all of them.
/// </summary>
public class CompositeBackfillExecutor : IBackfillExecutor
{
    private readonly IReadOnlyList<IBackfillExecutor> _inners;

    public CompositeBackfillExecutor(IReadOnlyList<IBackfillExecutor> inners)
    {
        _inners = inners;
    }

    public async Task SettleAsync(CancellationToken token)
    {
        foreach (var inner in _inners) await inner.SettleAsync(token).ConfigureAwait(false);
    }

    public async Task<bool> CatchUpAsync(Func<long> liveProgression, BackfillRequest request, CancellationToken token)
    {
        var pending = false;
        foreach (var inner in _inners)
        {
            pending |= await inner.CatchUpAsync(liveProgression, request, token).ConfigureAwait(false);
        }

        return pending;
    }

    public async Task HandOverAsync(long ceiling, BackfillRequest request, CancellationToken token)
    {
        foreach (var inner in _inners) await inner.HandOverAsync(ceiling, request, token).ConfigureAwait(false);
    }
}
