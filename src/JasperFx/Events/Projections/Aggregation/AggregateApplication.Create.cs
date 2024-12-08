using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections.Aggregation;

public partial class AggregateApplication<TAggregate, TQuerySession>
{
    private ImHashMap<Type, Func<IEvent, TQuerySession, ValueTask<TAggregate>>> _creators = ImHashMap<Type, Func<IEvent, TQuerySession, ValueTask<TAggregate>>>.Empty;
    
    public ValueTask<TAggregate> Create(IEvent e, TQuerySession session)
    {
        if (_creators.TryFind(e.EventType, out var creator))
        {
            return creator(e, session);
        }

        creator = determineCreator(e.EventType);
        _creators = _creators.AddOrUpdate(e.EventType, creator);

        return creator(e, session);
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
        return Create(new Event<T>(eventData), session);
    }

    private Func<IEvent,TQuerySession,ValueTask<TAggregate>> determineCreator(Type eventType)
    {
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TQuerySession), "session");

        Expression body = makeCreatorBody(e, session, eventType);
        if (body == null)
        {
            return (_, _) =>
                throw new InvalidEventToStartAggregateException(typeof(TAggregate), _projectionType, eventType);
        }


        var lambda = Expression.Lambda<Func<IEvent, TQuerySession, ValueTask<TAggregate>>>(body, e, session);
        return lambda.CompileFast();

    }

    private Expression? makeCreatorBody(Expression e, Expression session, Type eventType)
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

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        if (typeof(TAggregate).TryFindStaticMethod(CreateMethod, out var dataMethod, eventType))
        {
            var arguments = dataMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, dataMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (typeof(TAggregate).TryFindStaticMethod(CreateMethod, out var wrappedMethod, wrappedType))
        {
            var arguments = wrappedMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, wrappedMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }

        if (_projectionType.TryFindMethod(CreateMethod, out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            Expression instance = method.IsStatic ? null : Expression.Constant(_projection);
            return Expression.Call(instance, method, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (_projectionType.TryFindMethod(CreateMethod, out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            Expression instance = method.IsStatic ? null : Expression.Constant(_projection);
            return Expression.Call(instance, method, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }

        var defaultCtor = typeof(TAggregate).GetConstructors()
            .FirstOrDefault(x => !x.GetParameters().Any());

        if (defaultCtor != null)
        {
            return Expression.New(defaultCtor).MaybeWrapWithValueTask<TAggregate>();
        }

        return null;

    }
}