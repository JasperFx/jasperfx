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