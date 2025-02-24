using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections;

public partial class AggregateApplication<TAggregate, TQuerySession> : IAggregator<TAggregate, TQuerySession>, IMetadataApplication
{
    // This would be for external projections
    private readonly object _projection;
    private readonly Type _projectionType;
    
    private ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>> _applications = ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>>.Empty;
    private ImHashMap<Type, Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>> _creators = ImHashMap<Type, Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate>>>.Empty;
    private readonly CreateMethodCollection _createMethods;
    private readonly ApplyMethodCollection _applyMethods;
    private readonly ShouldDeleteMethodCollection _shouldDeleteMethods;
    private readonly IMetadataApplication _metadataApplication;

    public AggregateApplication()
    {
        _projection = null;
        _projectionType = null;

        _createMethods = new CreateMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _applyMethods = new ApplyMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _shouldDeleteMethods = new ShouldDeleteMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
    }

    public AggregateApplication(object projection)
    {
        _projection = projection;
        _metadataApplication = projection as IMetadataApplication ?? this;
        _projectionType = projection.GetType();
        
        _createMethods = new CreateMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _applyMethods = new ApplyMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _shouldDeleteMethods = new ShouldDeleteMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
    }

    object IMetadataApplication.ApplyMetadata(object aggregate, IEvent lastEvent)
    {
        return aggregate;
    }

    public IEnumerable<Type> AllEventTypes()
    {
        // TODO -- also get lambdas that are explicitly added
        return MethodCollection
            .AllEventTypes(_applyMethods, _createMethods, _shouldDeleteMethods)
            .Concat(_creators.Enumerate().Select(x => x.Key))
            .Concat(_applications.Enumerate().Select(x => x.Key))
            .Distinct().ToArray();
    }

    /// <summary>
    /// Really just for testing purposes
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="data"></param>
    /// <param name="session"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ValueTask<TAggregate?> ApplyByDataAsync<T>(TAggregate snapshot, T data, TQuerySession session)
    {
        var e = new Event<T>(data);
        return ApplyAsync(snapshot, e, session, CancellationToken.None);
    }

    public ValueTask<TAggregate?> ApplyAsync(TAggregate snapshot, IEvent e, TQuerySession session, CancellationToken token)
    {
        if (_applications.TryFind(e.EventType, out var application))
        {
            return application(snapshot, e, session, token);
        }

        application = determineApplication(e.EventType);
        _applications = _applications.AddOrUpdate(e.EventType, application);

        return application(snapshot, e, session, token);
    }


    private Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>> tryBuildShouldDelete(
        ParameterExpression snapshot, ParameterExpression e, ParameterExpression session, Type eventType,
        ParameterExpression cancellation)
    {
        Expression body = makeShouldDeleteBody(snapshot, e, session, eventType, cancellation);

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

    private Expression makeShouldDeleteBody(ParameterExpression snapshot, ParameterExpression e,
        ParameterExpression session, Type eventType, ParameterExpression cancellation)
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

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        // You would use null for static methods
        Expression caller = default(Expression);
        
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
    
    public void AssertNoInvalidMethods()
    {
        if (_applyMethods.IsEmpty() && _createMethods.IsEmpty() && _creators.IsEmpty && _applications.IsEmpty)
        {
            throw new InvalidProjectionException(
                $"No matching conventional Apply/Create methods for the {typeof(TAggregate).FullNameInCode()} aggregate.");
        }

        var invalidMethods =
            MethodCollection.FindInvalidMethods(_projectionType, _applyMethods, _createMethods, _shouldDeleteMethods)
                .Where(x => !x.Method.HasAttribute<JasperFxIgnoreAttribute>());

        if (invalidMethods.Any())
        {
            throw new InvalidProjectionException(this, invalidMethods);
        }
    }

    public async ValueTask<TAggregate> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TAggregate? snapshot, CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        foreach (var e in events)
        {
            if (snapshot == null)
            {
                snapshot = await Create(e, session, cancellation);
            }
            else
            {
                snapshot = await ApplyAsync(snapshot, e, session, cancellation);
            }
        }

        if (snapshot != null)
        {
            _metadataApplication.ApplyMetadata(snapshot, events.Last());
        }

        return snapshot;
    }
}