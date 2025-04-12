using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections;

/// <summary>
/// Used by JasperFx.Events internals to decide whether an exception thrown by an
/// asynchronous projection or subscription is a transient error or an event application error
/// </summary>
public static class ProjectionExceptions
{
    private static readonly List<Type> _transientExceptionTypes = new();

    /// <summary>
    /// Register an exception type as a transient type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public static void RegisterTransientExceptionType<T>() where T : Exception
    {
        _transientExceptionTypes.Fill(typeof(T));
    }
    
    public static bool IsExceptionTransient(Exception exception)
    {
        if (exception is InvalidEventToStartAggregateException) return false;
        
        if (_transientExceptionTypes.Any(x => exception.GetType().CanBeCastTo(x)))
        {
            return true;
        }

        return false;
    }
}