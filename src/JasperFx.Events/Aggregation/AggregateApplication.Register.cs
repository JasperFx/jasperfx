using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Aggregation;

internal partial class AggregateApplication<TAggregate, TQuerySession>
{
    public void CreateEvent<TEvent>(Func<TEvent,TAggregate> creator) where TEvent : class
    {
        createEvent<TEvent>(creator);
    }

    public void CreateEvent<TEvent>(Func<TEvent,TQuerySession,Task<TAggregate>> creator) where TEvent : class
    {
        createEvent<TEvent>(creator);
    }

    private void createEvent<TEvent>(object creator) where TEvent : class
    {
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");

        var eventType = typeof(TEvent).Closes(typeof(IEvent<>))
            ? typeof(TEvent).GetGenericArguments()[0]
            : typeof(TEvent);
        
        var caller = Expression.Constant(creator);
        var method = creator.GetType().GetMethod("Invoke");
        
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
        var strongTypedEvent = Expression.Convert(e, wrappedType);
        var data = Expression.Call(strongTypedEvent, getData);
        
        Func<ParameterInfo, Expression> buildParameter = p =>
        {
            if (p.ParameterType == typeof(TEvent)) return data;
            if (p.ParameterType == wrappedType) return strongTypedEvent;
            if (p.ParameterType == typeof(TQuerySession)) return session;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
        var body = Expression.Call(caller, method, arguments).MaybeWrapWithValueTask<TAggregate>();
        var lambda = Expression.Lambda<Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>>(body, e, session, cancellation).CompileFast();

        _creators = _creators.AddOrUpdate(eventType, lambda);
    }

    public void DeleteEvent<TEvent>(Func<TEvent,bool> shouldDelete) where TEvent : class
    {
        deleteEvent<TEvent>(shouldDelete);
    }

    public void DeleteEvent<TEvent>(Func<TAggregate,TEvent,bool> shouldDelete) where TEvent : class
    {
        deleteEvent<TEvent>(shouldDelete);
    }

    public void DeleteEventAsync<TEvent>(Func<TQuerySession, TAggregate,TEvent,Task<bool>> shouldDelete) where TEvent : class
    {
        deleteEvent<TEvent>(shouldDelete);
    }

    private void deleteEvent<TEvent>(object shouldDelete) where TEvent : class
    {
        var snapshot = Expression.Parameter(typeof(TAggregate), "snapshot");
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");

        var eventType = typeof(TEvent).Closes(typeof(IEvent<>))
            ? typeof(TEvent).GetGenericArguments()[0]
            : typeof(TEvent);
        
        var caller = Expression.Constant(shouldDelete);
        var method = shouldDelete.GetType().GetMethod("Invoke");
        
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
        var strongTypedEvent = Expression.Convert(e, wrappedType);
        var data = Expression.Call(strongTypedEvent, getData);
        
        Func<ParameterInfo, Expression> buildParameter = p =>
        {
            if (p.ParameterType == eventType) return data;
            if (p.ParameterType == wrappedType) return strongTypedEvent;
            if (p.ParameterType == typeof(TQuerySession)) return session;
            if (p.ParameterType == typeof(TAggregate)) return snapshot;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
        var body = Expression.Call(caller, method, arguments).MaybeWrapWithValueTask<bool>();
        var func = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<bool>>>(body, snapshot, e, session, cancellation).CompileFast();

        _applications = _applications.AddOrUpdate(eventType, async (a, @event, s, t) =>
        {
            if (await func(a, @event, s, t))
            {
                return default;
            }

            return a;
        });
    }

    public void ProjectEvent<TEvent>(Action<TAggregate> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }

    public void ProjectEvent<TEvent>(Action<TAggregate,TEvent> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }

    public void ProjectEvent<TEvent>(Func<TAggregate,TEvent,TAggregate> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }

    public void ProjectEvent<TEvent>(Func<TAggregate,TAggregate> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }

    public void ProjectEvent<TEvent>(Action<TQuerySession, TAggregate, TEvent> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }


    public void ProjectEvent<TEvent>(Func<TQuerySession,TAggregate,TEvent,Task> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }
    
    public void ProjectEvent<TEvent>(Func<TQuerySession,TAggregate,TEvent,Task<TAggregate>> handler) where TEvent : class
    {
        projectEvent<TEvent>(handler);
    }

    private void projectEvent<TEvent>(object handler) where TEvent : class
    {
        var snapshot = Expression.Parameter(typeof(TAggregate), "snapshot");
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");

        var eventType = typeof(TEvent).Closes(typeof(IEvent<>))
            ? typeof(TEvent).GetGenericArguments()[0]
            : typeof(TEvent);
        
        var caller = Expression.Constant(handler);
        var method = handler.GetType().GetMethod("Invoke");
        
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
        var strongTypedEvent = Expression.Convert(e, wrappedType);
        var data = Expression.Call(strongTypedEvent, getData);
        
        Func<ParameterInfo, Expression> buildParameter = p =>
        {
            if (p.ParameterType == eventType) return data;
            if (p.ParameterType == wrappedType) return strongTypedEvent;
            if (p.ParameterType == typeof(TQuerySession)) return session;
            if (p.ParameterType == typeof(TAggregate)) return snapshot;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
        var body = Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        var func = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>>(body, snapshot, e, session, cancellation).CompileFast();

        _applications = _applications.AddOrUpdate(eventType, func);
    }
}