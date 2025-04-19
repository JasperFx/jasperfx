using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Aggregation;

internal partial class AggregateApplication<TAggregate, TQuerySession>
{
        private Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>? tryBuildShouldDelete(
        ParameterExpression snapshot, ParameterExpression e, ParameterExpression session, Type eventType,
        ParameterExpression cancellation)
    {
        Expression? body = makeShouldDeleteBody(snapshot, e, session, eventType, cancellation);

        if (body == null) return null;
        
        var lambda = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<bool>>>(body, snapshot, e, session, cancellation);
        var func = lambda.CompileFast();

        return async (a, @event, s, t) =>
        {
            if (await func(a, @event, s, t))
            {
                return default;
            }

            return a;
        };
    }

    private Expression? makeShouldDeleteBody(ParameterExpression snapshot, ParameterExpression e,
        ParameterExpression session, Type eventType, ParameterExpression cancellation)
    {
        
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data))!.GetMethod!;
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

        // You would use null for static methods
        Expression? caller = default(Expression);
        
        if (typeof(TAggregate).TryFindMethod(ShouldDeleteMethodCollection.MethodName, out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<bool>(snapshot);
        }
        
        if (typeof(TAggregate).TryFindMethod(ShouldDeleteMethodCollection.MethodName, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<bool>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ShouldDeleteMethodCollection.MethodName, out method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<bool>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ShouldDeleteMethodCollection.MethodName, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<bool>(snapshot);
        }

        return null;
    }
    
}