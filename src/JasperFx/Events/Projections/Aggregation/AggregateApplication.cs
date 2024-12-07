using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections.Aggregation;

// TODO -- add CancellationToken to this? Ick, but yes.
// TODO -- set version as well?
// TODO -- apply metadata
// TODO -- introduce string constants
public class AggregateApplication<TAggregate, TQuerySession>
{
    // This would be for external projections
    private readonly object _projection;
    private readonly Type _projectionType;
    
    private ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, ValueTask<TAggregate?>>> _applications = ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, ValueTask<TAggregate?>>>.Empty;
    private ImHashMap<Type, Func<IEvent, TQuerySession, ValueTask<TAggregate>>> _creators = ImHashMap<Type, Func<IEvent, TQuerySession, ValueTask<TAggregate>>>.Empty;
    
    public AggregateApplication()
    {
        _projection = null;
        _projectionType = null;
    }

    public AggregateApplication(object projection)
    {
        _projection = projection;
        _projectionType = projection.GetType();
    }
    
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
        
        if (typeof(TAggregate).TryFindMethod("Apply", out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (typeof(TAggregate).TryFindMethod("Apply", out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = snapshot;

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod("Apply", out method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }
        
        if (_projectionType.TryFindMethod("Apply", out method, wrappedType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            if (!method.IsStatic) caller = Expression.Constant(_projection);

            return Expression.Call(caller, method, arguments).ReturnWithValueTask<TAggregate>(snapshot);
        }

        return null;
    }

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

        if (typeof(TAggregate).TryFindStaticMethod("Create", out var dataMethod, eventType))
        {
            var arguments = dataMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, dataMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (typeof(TAggregate).TryFindStaticMethod("Create", out var wrappedMethod, wrappedType))
        {
            var arguments = wrappedMethod.GetParameters().Select(x => buildParameter(x)).ToArray();
            return Expression.Call(null, wrappedMethod, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }

        if (_projectionType.TryFindMethod("Create", out var method, eventType))
        {
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            Expression instance = method.IsStatic ? null : Expression.Constant(_projection);
            return Expression.Call(instance, method, arguments).MaybeWrapWithValueTask<TAggregate?>();
        }
        
        if (_projectionType.TryFindMethod("Create", out method, wrappedType))
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

public class InvalidEventToStartAggregateException : Exception
{
    public static string ToMessage(Type aggregateType, Type projectionType, Type eventType)
    {
        var writer = new StringWriter();
        writer.WriteLine($"An aggregation projection for aggregate type {aggregateType.FullNameInCode()} cannot be created by event type {eventType.FullNameInCode()}");
        writer.WriteLine("This error usually occurs when an unexpected event type is the first event encountered for this type of projected aggregate");
        writer.WriteLine("The valid options for starting a new projected aggregate for the event type would be:");
        writer.WriteLine($"An empty, public constructor of signature new {aggregateType.ShortNameInCode()}()");
        writer.WriteLine($"A static method on {aggregateType.ShortNameInCode()} of signature public static {aggregateType.ShortNameInCode()} Create({eventType.ShortNameInCode()}) or public static {aggregateType.ShortNameInCode()} Create({typeof(IEvent<>).MakeGenericType(eventType).ShortNameInCode()})");

        if (projectionType != null)
        {
            writer.WriteLine($"{projectionType.FullNameInCode()}.Create({eventType.ShortNameInCode()})");
            writer.WriteLine($"{projectionType.FullNameInCode()}.Create({typeof(IEvent<>).MakeGenericType(eventType).ShortNameInCode()})");
        }

        // TODO -- link to documentation page explaining this better
        
        return writer.ToString();
    }
    
    public InvalidEventToStartAggregateException(Type aggregateType, Type projectionType, Type eventType) : base(ToMessage(aggregateType, projectionType, eventType))
    {
    }
}

