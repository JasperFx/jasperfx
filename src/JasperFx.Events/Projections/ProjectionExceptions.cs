using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

/// <summary>
/// Used by JasperFx.Events internals to decide whether an exception thrown by an
/// asynchronous projection or subscription is a transient error or an event application error
/// </summary>
public static class ProjectionExceptions
{
    private static readonly List<Type> _transientExceptionTypes = new();

    /// <summary>
    /// Register an exception type as a transient type. Every exception of this type, or a subtype, is treated
    /// as transient regardless of what <see cref="TransientFailures" /> makes of it.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public static void RegisterTransientExceptionType<T>() where T : Exception
    {
        _transientExceptionTypes.Fill(typeof(T));
    }

    /// <summary>
    /// Whether an exception thrown by projection code should be rethrown for the daemon's resilience pipeline to
    /// retry (true), or wrapped as an <see cref="ApplyEventException" /> that pauses the shard or, with
    /// SkipApplyErrors, dead-letters the event (false).
    ///
    /// <para>
    /// jasperfx#1019: transient when the exception is of a type registered with
    /// <see cref="RegisterTransientExceptionType{T}" />, <b>or</b> when <see cref="TransientFailures.IsTransient" />
    /// says so -- so a store that only registers a classifier (Polecat, Fisher) no longer dead-letters a deadlock or
    /// SQLITE_BUSY that would have applied fine on retry, and this agrees with <see cref="ShardFailure.IsTransient" />.
    /// </para>
    /// </summary>
    public static bool IsExceptionTransient(Exception exception)
    {
        if (exception is InvalidEventToStartAggregateException) return false;

        // Already attributed to one event by an inner layer. Rethrowing it as "transient" would only send it to the
        // skipping path anyway, which catches ApplyEventException by type and dead-letters it
        if (exception is ApplyEventException) return false;

        if (_transientExceptionTypes.Any(x => exception.GetType().CanBeCastTo(x)))
        {
            return true;
        }

        return TransientFailures.IsTransient(exception);
    }

    /// <summary>
    /// jasperfx#1029: whether <paramref name="exception" /> is a <b>systemic</b> database failure -- a
    /// <see cref="DbException" /> anywhere in its graph that <see cref="TransientFailures" /> does not classify
    /// as transient. A missing column or table, a permissions problem, a broken migration.
    ///
    /// <para>
    /// Such a failure is not the fault of the event it surfaced on: the next event would fail the same way. So
    /// the daemon never dead-letters it, even under <see cref="ErrorHandlingOptions.SkipApplyErrors" />. It
    /// pauses the shard instead, with a <see cref="ShardFailure" /> whose <see cref="ShardFailure.IsTransient" />
    /// is false and whose <see cref="ShardFailure.RootExceptionType" /> names the database's own exception.
    /// Skipping would walk the projection past every event in turn and leave it silently empty.
    /// </para>
    ///
    /// <para>
    /// This is what makes it safe for a store to stop registering its driver's whole exception hierarchy with
    /// <see cref="RegisterTransientExceptionType{T}" />: a permanent error then fails fast to a pause instead
    /// of burning the resilience budget first, and still never reaches the dead-letter queue.
    /// </para>
    /// </summary>
    public static bool IsSystemicDatabaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return containsDbException(exception, 0) && !TransientFailures.IsTransient(exception);
    }

    // Same bound as TransientFailures: nothing real is 32 levels deep
    private const int MaxDepth = 32;

    private static bool containsDbException(Exception exception, int depth)
    {
        if (depth > MaxDepth) return false;

        if (exception is DbException) return true;

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(x => containsDbException(x, depth + 1));
        }

        return exception.InnerException != null && containsDbException(exception.InnerException, depth + 1);
    }
}
