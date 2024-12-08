using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections.Aggregation;

public partial class AggregateApplication<TAggregate, TQuerySession>
{
    private ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, ValueTask<TAggregate?>>> _applications = ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, ValueTask<TAggregate?>>>.Empty;

    public async ValueTask<TAggregate?> Apply(TAggregate? snapshot, IReadOnlyList<IEvent> events, TQuerySession session)
    {
        var startingIndex = 0;
        if (snapshot == null)
        {
            snapshot = await Create(events[0], session).ConfigureAwait(false);
            startingIndex = 1;
        }
        
        // Need to skip the first one
        for (int i = startingIndex; i < events.Count; i++)
        {
            if (snapshot == null)
            {
                snapshot = await Create(events[i], session);
            }
            else
            {
                snapshot = await ApplyAsync(snapshot, events[i], session).ConfigureAwait(false);
            }
            
            // Deletes?
            if (snapshot == null) return default(TAggregate);
        }

        return snapshot;
    }

    public ValueTask<TAggregate?> ApplyByDataAsync<T>(TAggregate snapshot, T data, TQuerySession session)
    {
        var e = new Event<T>(data);
        return ApplyAsync(snapshot, e, session);
    }

    public ValueTask<TAggregate?> ApplyAsync(TAggregate snapshot, IEvent e, TQuerySession session)
    {
        if (_applications.TryFind(e.EventType, out var application))
        {
            return application(snapshot, e, session);
        }

        application = determineApplication(e.EventType);
        _applications = _applications.AddOrUpdate(e.EventType, application);

        return application(snapshot, e, session);
    }

    private Func<TAggregate,IEvent,TQuerySession,ValueTask<TAggregate?>> determineApplication(Type eventType)
    {
        var snapshot = Expression.Parameter(typeof(TAggregate), "snapshot");
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");

        Expression body = makeApplyBody(snapshot, e, session, eventType);
        if (body == null)
        {
            return (x, _, _) => new ValueTask<TAggregate?>(x);
        }
        
        var lambda = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, ValueTask<TAggregate>>>(body, snapshot, e, session);
        return lambda.CompileFast();
    }

    private Expression makeApplyBody(ParameterExpression snapshot, ParameterExpression e, ParameterExpression session, Type eventType)
    {
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

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        // You would use null for static methods
        Expression caller = default(Expression);
        
        if (typeof(TAggregate).TryFindMethod(ApplyMethod, out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (typeof(TAggregate).TryFindMethod(ApplyMethod, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ApplyMethod, out method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ApplyMethod, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }

        return null;
    }


}