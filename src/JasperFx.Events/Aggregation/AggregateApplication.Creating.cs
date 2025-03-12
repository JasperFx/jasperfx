using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

internal partial class AggregateApplication<TAggregate, TQuerySession>
{
    public ValueTask<TAggregate> Create(IEvent e, TQuerySession session, CancellationToken token)
    {
        if (_creators.TryFind(e.EventType, out var creator))
        {
            return creator(e, session, token);
        }

        creator = determineCreator(e.EventType);
        _creators = _creators.AddOrUpdate(e.EventType, creator);

        return creator(e, session, token);
    }
    
    /// <summary>
    /// Test helper to start from event data
    /// </summary>
    /// <param name="eventData"></param>
    /// <param name="session"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ValueTask<TAggregate> CreateByData<T>(T eventData, TQuerySession session)
    {
        return Create(new Event<T>(eventData), session, CancellationToken.None);
    }

    private Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>> determineCreator(Type eventType)
    {
        if (!_createMethods.Methods.Any(x => x.EventType == eventType))
        {
            // Try to find a creator from an abstraction or interface
            var superType = AllEventTypes().FirstOrDefault(type => eventType.CanBeCastTo(type) && type != eventType);
            if (superType != null)
            {
                if (!_creators.TryFind(superType, out var superCreator))
                {
                    superCreator = determineCreator(superType);
                    _creators = _creators.AddOrUpdate(superType, superCreator);
                }
                
                return (@event, s, token) => superCreator(@event, s, token);
            }
        }
        
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");
        
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        
        Expression body = makeCreatorBody(e, session, eventType, cancellation);
        if (body == null)
        {
            var defaultCtor = typeof(TAggregate).GetConstructors(bindingFlags)
                .FirstOrDefault(x => !x.GetParameters().Any());

            if (defaultCtor != null)
            {
                var builder = Expression.Lambda<Func<TAggregate>>(Expression.New(defaultCtor)).CompileFast();
                
                if (_applications.TryFind(eventType, out var superApplier))
                {
                    return (@event, s, t) => superApplier(builder(), @event, s, t);
                }
                
                var snapshot = Expression.Parameter(typeof(TAggregate), "snapshot");
                
                // Use the Apply() if there is one
                var applyBody = makeApplyBody(snapshot, e, session, eventType, cancellation);
                if (applyBody != null)
                {
                    var apply = Expression.Lambda<Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>>(applyBody, snapshot, e, session, cancellation).CompileFast();
                    return (@event, s, t) => apply(builder(), @event, s, t);
                }
                
                body = Expression.New(defaultCtor).MaybeWrapWithValueTask<TAggregate>();
            }
            else
            {
                return (_, _, _) =>
                    throw new InvalidEventToStartAggregateException(typeof(TAggregate), _projectionType, eventType);
            }
        }


        var lambda = Expression.Lambda<Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>>(body, e, session, cancellation);
        return lambda.CompileFast();

    }

    private Expression? makeCreatorBody(Expression e, Expression session, Type eventType, Expression cancellation)
    {
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
        var strongTypedEvent = Expression.Convert(e, wrappedType);
        var data = Expression.Call(strongTypedEvent, getData) ;

        if (typeof(TAggregate).TryFindConstructor(out var dataCtor, eventType))
        {
            return Expression.New(dataCtor, data).MaybeWrapWithValueTask<TAggregate?>();
        }

        if (typeof(TAggregate).TryFindConstructor(out var wrappedCtor, wrappedType))
        {
            return Expression.New(wrappedCtor, strongTypedEvent).MaybeWrapWithValueTask<TAggregate?>();
        }

        Func<ParameterInfo, Expression> buildParameter = p =>
        {
            if (p.ParameterType == eventType) return data;
            if (p.ParameterType == wrappedType) return strongTypedEvent;
            if (p.ParameterType == typeof(TQuerySession)) return session;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;
            if (p.ParameterType == typeof(IEvent)) return e;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        if (typeof(TAggregate).TryFindStaticMethod(CreateMethodCollection.MethodName, out var dataMethod, eventType))
        {
            var arguments = dataMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, dataMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (typeof(TAggregate).TryFindStaticMethod(CreateMethodCollection.MethodName, out var wrappedMethod, wrappedType))
        {
            var arguments = wrappedMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, wrappedMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }

        if (_projectionType.TryFindMethod(CreateMethodCollection.MethodName, out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            Expression instance = method.IsStatic ? null : Expression.Constant(_projection);
            return Expression.Call(instance, method, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (_projectionType.TryFindMethod(CreateMethodCollection.MethodName, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            Expression instance = method.IsStatic ? null : Expression.Constant(_projection);
            return Expression.Call(instance, method, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }

        return null;

    }
    
}