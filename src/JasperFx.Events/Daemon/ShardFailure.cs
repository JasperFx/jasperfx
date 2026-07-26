using JasperFx.Core.Reflection;

namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#565: WHY a projection/subscription shard was paused or stopped, in a form that survives
/// leaving the process.
///
/// <para>
/// Before this, an external supervisor — Wolverine's <c>EventSubscriptionAgent</c> wrapping a shard as a
/// distributed agent, or CritterWatch polling the store — could see <see cref="AgentStatus.Paused"/> and
/// nothing else. Progress silently flatlined and no alert could say what to do about it. The daemon has
/// always had the exception in hand (<see cref="SubscriptionAgent.ReportCriticalFailureAsync(Exception)"/>
/// is the single funnel every failure path reaches); this is that knowledge, classified and made
/// portable.
/// </para>
///
/// <para>
/// Deliberately a plain value rather than an <see cref="Exception"/>: consumers serialize it, persist it
/// onto the store's extended progression row, and render it. <see cref="ShardState.Exception"/> still
/// carries the live exception for in-process observers.
/// </para>
/// </summary>
public record ShardFailure
{
    /// <summary>
    /// What kind of failure this was, and therefore what an operator should do about it.
    /// </summary>
    public required ShardFailureCategory Category { get; init; }

    /// <summary>
    /// Type of the exception the daemon caught, as a code-readable full name.
    /// </summary>
    public required string ExceptionType { get; init; }

    /// <summary>
    /// Type of the innermost exception, which for the wrapping cases (<see cref="ApplyEventException"/>,
    /// <see cref="ShardStopException"/>, an <see cref="AggregateException"/>) is the one that actually
    /// names the fault — the same choice <see cref="DeadLetterEvent.ExceptionType"/> makes. Equal to
    /// <see cref="ExceptionType"/> when nothing was wrapped.
    /// </summary>
    public required string RootExceptionType { get; init; }

    /// <summary>
    /// <see cref="Exception.Message"/> of the caught exception. Short enough for an alert subject line.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The full <see cref="Exception.ToString()"/>, inner exceptions and stack traces included. This is
    /// what <see cref="ShardState.PauseReason"/> has always carried, kept intact so nothing an operator
    /// used to be able to read is lost.
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>
    /// The single event that broke this shard, when the failure could be attributed to one — i.e. every
    /// category except <see cref="ShardFailureCategory.ProgressionOutOfOrder"/> and
    /// <see cref="ShardFailureCategory.Other"/>. Null otherwise.
    /// </summary>
    public EventFailureDetails? Event { get; init; }

    /// <summary>
    /// When the failure was observed.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// <see cref="DeadLetterEvent.Id"/> of a dead-letter row describing this same failing event, if the
    /// caller has one.
    ///
    /// <para>
    /// Null on the daemon's pause path, and that is on purpose: a paused shard has NOT skipped the event,
    /// so writing a dead letter there would inflate the dead-letter counts that stores report as their
    /// "this projection is unhealthy" signal, and would rewrite a row on every restart attempt. When the
    /// same event is later skipped (<see cref="ErrorHandlingOptions.SkipApplyErrors"/> and friends), the
    /// dead letter it produces is correlatable through
    /// <see cref="DeadLetterEvent.DescribesSameFailureAs"/> — projection name, shard key, sequence and
    /// tenant all line up. This slot exists for the paths that DO have a row in hand.
    /// </para>
    /// </summary>
    public Guid? DeadLetterEventId { get; init; }

    /// <summary>
    /// Classify a caught exception. The single place any of this is decided, so the daemon, the stores and
    /// an external supervisor never disagree about what a failure was.
    ///
    /// <para>
    /// The failing event is found by walking the whole exception graph — inner exceptions and
    /// <see cref="AggregateException.InnerExceptions"/> alike — for an
    /// <see cref="IEventFailureContext"/>, because the per-event exceptions routinely arrive wrapped
    /// (<see cref="ShardStopException"/> around an <see cref="ApplyEventException"/>, an
    /// <see cref="AggregateException"/> of several apply failures). The category comes from that context,
    /// so a store's exception declares its own kind rather than the daemon sniffing type names.
    /// </para>
    /// </summary>
    public static ShardFailure For(Exception exception, DateTimeOffset occurredAt)
    {
        var context = findEventFailure(exception);
        var category = context?.Category
                       ?? (hasProgressionOutOfOrder(exception)
                           ? ShardFailureCategory.ProgressionOutOfOrder
                           : ShardFailureCategory.Other);

        return new ShardFailure
        {
            Category = category,
            ExceptionType = exception.GetType().FullNameInCode(),
            RootExceptionType = rootOf(exception).GetType().FullNameInCode(),
            Message = exception.Message,
            Detail = exception.ToString(),
            Event = context == null ? null : EventFailureDetails.From(context),
            OccurredAt = occurredAt
        };
    }

    private static IEventFailureContext? findEventFailure(Exception exception)
    {
        if (exception is IEventFailureContext context) return context;

        if (exception is AggregateException aggregate)
        {
            // Several apply failures in one batch is the common shape here. First one wins: the shard
            // stops at the earliest failing event anyway, and the full text of all of them is in Detail.
            foreach (var inner in aggregate.InnerExceptions.OrderBy(sequenceOf))
            {
                var found = findEventFailure(inner);
                if (found != null) return found;
            }

            return null;
        }

        return exception.InnerException == null ? null : findEventFailure(exception.InnerException);
    }

    private static long sequenceOf(Exception exception)
        => findEventFailure(exception)?.Sequence ?? long.MaxValue;

    private static bool hasProgressionOutOfOrder(Exception exception)
    {
        if (exception is ProgressionProgressOutOfOrderException) return true;

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(hasProgressionOutOfOrder);
        }

        return exception.InnerException != null && hasProgressionOutOfOrder(exception.InnerException);
    }

    private static Exception rootOf(Exception exception)
    {
        // An AggregateException's "root" is ambiguous; treat the first inner as the representative one,
        // matching which failure findEventFailure would have reported.
        while (true)
        {
            if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                exception = aggregate.InnerExceptions.OrderBy(sequenceOf).First();
                continue;
            }

            if (exception.InnerException == null) return exception;

            exception = exception.InnerException;
        }
    }

    public override string ToString()
    {
        return Event == null
            ? $"{Category}: {Message}"
            : $"{Category} on {Event}: {Message}";
    }
}
