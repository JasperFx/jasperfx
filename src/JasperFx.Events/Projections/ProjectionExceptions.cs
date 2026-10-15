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
}
