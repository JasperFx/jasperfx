using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Aggregation;

public partial class AggregateApplication<TAggregate, TQuerySession>
{
    private Func<TAggregate,IEvent,TQuerySession, CancellationToken, ValueTask<TAggregate?>> determineApplication(Type eventType)
    {
        if (!_applyMethods.Methods.Any(x =>
                x.EventType == eventType || _shouldDeleteMethods.Methods.Any(slot => slot.EventType == eventType)))
        {
            // Try to find an applier from an abstraction or interface
            var superType = AllEventTypes().FirstOrDefault(type => eventType.CanBeCastTo(type) && type != eventType);
            if (superType != null)
            {
                if (!_applications.TryFind(superType, out var superApplier))
                {
                    superApplier = determineApplication(superType);
                    _applications = _applications.AddOrUpdate(superType, superApplier);
                }
                
                return (a, @event, s, token) => superApplier(a, @event, s, token);
            }
        }
        
        var snapshot = Expression.Parameter(typeof(TAggregate), "snapshot");
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");

        Expression body = makeApplyBody(snapshot, e, session, eventType, cancellation);
        if (body == null)
        {
            var shouldDelete = tryBuildShouldDelete(snapshot, e, session, eventType, cancellation);
            if (shouldDelete != null) return shouldDelete;
            
            // Try to find a creator from an abstraction or interface
            var superType = AllEventTypes().FirstOrDefault(t => eventType.CanBeCastTo(t) && t != eventType);
            if (superType != null)
            {
                if (!_applications.TryFind(eventType, out var superApplier))
                {
                    superApplier = determineApplication(eventType);
                    _applications = _applications.AddOrUpdate(superType, superApplier);
                }
                
                return (a, @event, s, token) => superApplier(a, @event, s, token);
            }

            return (x, _, _, _) => new ValueTask<TAggregate?>(x);
        }
        
        var lambda = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>>(body, snapshot, e, session, cancellation);
        return lambda.CompileFast();
    }
    
        private Expression makeApplyBody(Expression snapshot, ParameterExpression e, ParameterExpression session,
        Type eventType, ParameterExpression cancellation)
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
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;
            if (p.ParameterType == typeof(IEvent)) return e;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        // You would use null for static methods
        Expression caller = default(Expression);
        
        if (typeof(TAggregate).TryFindMethod(ApplyMethodCollection.MethodName, out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (typeof(TAggregate).TryFindMethod(ApplyMethodCollection.MethodName, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ApplyMethodCollection.MethodName, out method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod(ApplyMethodCollection.MethodName, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }

        return null;
    }

}