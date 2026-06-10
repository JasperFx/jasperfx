using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

/// <summary>
/// Single-pass rebuild executor for a <see cref="CompositeProjection{TOperations,TQuerySession}" />.
/// Reads the event store exactly once from the requested floor up to the current high-water ceiling
/// and fans every page through the composite execution, which dispatches it to all member stages and
/// commits their combined work as one batch per page. This collapses what would otherwise be N
/// independent rebuild passes (one per member) into a single ordered pass over the events.
///
/// This is tenancy-free (jasperfx#407 Phase A). The actual event loading and batch persistence live in
/// the <see cref="IEventLoader" /> / composite <see cref="ISubscriptionExecution" /> supplied by the
/// concrete event store (Marten/Polecat), so the document-level rebuild correctness is verified in the
/// store implementation; here we own the single-pass loop and the all-or-nothing stop semantics.
/// </summary>
internal class CompositeReplayExecutor : IReplayExecutor
{
    private readonly IEventDatabase _database;
    private readonly ISubscriptionExecution _execution;
    private readonly IEventLoader _loader;
    private readonly ILogger _logger;
    private readonly AsyncOptions _options;
    private readonly ShardName _shardName;

    public CompositeReplayExecutor(ShardName shardName, IEventLoader loader, ISubscriptionExecution execution,
        IEventDatabase database, AsyncOptions options, ILogger logger)
    {
        _shardName = shardName;
        _loader = loader;
        _execution = execution;
        _database = database;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(SubscriptionExecutionRequest request, ISubscriptionController controller,
        CancellationToken cancellation)
    {
        // The composite shard is driven directly here rather than through the agent's command/loader
        // ping-pong, so the events are read once and fanned to every member stage in a single pass.
        if (controller is not ISubscriptionAgent agent)
        {
            throw new ArgumentException(
                $"{nameof(CompositeReplayExecutor)} requires the controller to also be an {nameof(ISubscriptionAgent)}",
                nameof(controller));
        }

        _execution.Mode = request.Mode;

        // marten#4717: a tenant-scoped composite must replay only up to ITS tenant's high-water, supplied
        // via StartingHighWater. A store-global composite (StartingHighWater == null) keeps reading the
        // store-wide max(seq_id) from mt_events — the marten#4705 fix that made single-tenant partitioned
        // composites replay past the never-advanced global mt_events_sequence.
        var ceiling = request.StartingHighWater
            ?? await _database.FetchHighestEventSequenceNumber(cancellation).ConfigureAwait(false);
        var floor = request.Floor;

        if (ceiling <= floor)
        {
            // Nothing to replay -- still advance the composite progression so it reads as caught up.
            await controller.MarkSuccessAsync(ceiling).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Starting single-pass composite rebuild for {ShardName} over events {Floor} to {Ceiling}",
            _shardName.Identity, floor, ceiling);

        while (floor < ceiling && !cancellation.IsCancellationRequested)
        {
            var eventRequest = new EventRequest
            {
                Floor = floor,
                HighWater = ceiling,
                BatchSize = _options.BatchSize,
                Name = _shardName,
                ErrorOptions = request.ErrorHandling,
                Runtime = request.Runtime,
                Metrics = agent.Metrics
            };

            var page = await _loader.LoadAsync(eventRequest, cancellation).ConfigureAwait(false);
            page.HighWaterMark = ceiling;

            if (page.Count == 0)
            {
                // No more matching events below the ceiling; advance progression to the ceiling and finish.
                await controller.MarkSuccessAsync(ceiling).ConfigureAwait(false);
                break;
            }

            // A top-level Individual range makes the composite execution commit the combined member work
            // and advance progression (MarkSuccessAsync) as one unit for this page.
            var range = new EventRange(agent, page.Floor, page.Ceiling) { Events = page };
            await _execution.ProcessRangeAsync(range).ConfigureAwait(false);

            // The composite execution swallows a member failure and pauses/stops the agent rather than
            // throwing. Honor the all-or-nothing contract: stop the single pass without advancing further.
            if (agent.Status != AgentStatus.Running)
            {
                _logger.LogWarning(
                    "Stopping single-pass composite rebuild for {ShardName} at {Floor}; agent status is {Status}",
                    _shardName.Identity, page.Floor, agent.Status);
                return;
            }

            floor = page.Ceiling;
        }
    }
}
