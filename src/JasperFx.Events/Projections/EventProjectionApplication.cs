using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;

namespace JasperFx.Events.Projections;

public interface IEntityStorage<TOperations>
{
    void Store<T>(TOperations ops, T entity);
}

public class EventProjectionApplication<TOperations>
{
    private readonly IEntityStorage<TOperations> _entity;
    private readonly ProjectMethodCollection _projectMethods;
    private readonly CreateMethodCollection _createMethods;
    private ImHashMap<Type, Func<TOperations, IEvent, CancellationToken, ValueTask>> _applications 
        = ImHashMap<Type, Func<TOperations, IEvent, CancellationToken, ValueTask>>.Empty;

    private Type _projectionType;

    public EventProjectionApplication(IEntityStorage<TOperations> entityStorage)
    {
        _entity = entityStorage;
        _projectionType = entityStorage.GetType();
        _projectMethods = new ProjectMethodCollection(_projectionType);
        _createMethods = new CreateMethodCollection(_projectionType);
    }

    public IEnumerable<Type> AllEventTypes()
    {
        return MethodCollection
            .AllEventTypes(_projectMethods, _createMethods)
            .Concat(_applications.Enumerate().Select(x => x.Key))
            .Distinct().ToArray();
    }

    public ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken token)
    {
        if (_applications.TryFind(e.EventType, out var application))
        {
            return application(operations, e, token);
        }

        application = determineApplication(e.EventType);
        _applications = _applications.AddOrUpdate(e.EventType, application);

        return application(operations, e, token);
    }

    public IEnumerable<Type> PublishedTypes()
    {
        return _createMethods.Methods.Select(slot =>
        {
            if (slot.ReturnType.Closes(typeof(Task<>))) return slot.ReturnType.GetGenericArguments()[0];
            if (slot.ReturnType.Closes(typeof(ValueTask<>))) return slot.ReturnType.GetGenericArguments()[0];
            return slot.ReturnType;
        });
    }

    private Func<TOperations,IEvent,CancellationToken,ValueTask> determineApplication(Type eventType)
    {
        var projects = _projectMethods.Methods.Where(x => x.EventType == eventType).Select(x => buildApplication(eventType, (MethodInfo)x.Method));
        var creates = _createMethods.Methods.Where(x => x.EventType == eventType).Select(x => buildCreator(eventType, (MethodInfo)x.Method));
        
        var list = new List<Func<TOperations, IEvent, CancellationToken, ValueTask>>();
        list.AddRange(projects);
        list.AddRange(creates);
        
        if (!list.Any())
        {
            // look for base types
            var superType = AllEventTypes().FirstOrDefault(x => eventType.CanBeCastTo(x) && x != eventType);
            if (superType != null)
            {
                if (_applications.TryFind(superType, out var func))
                {
                    return (ops, @event, c) => func(ops, @event, c);
                }
            }
        }

        if (list.Count == 1) return list[0];

        return async (o, e, c) =>
        {
            foreach (var func in list)
            {
                await func(o, e, c);
            }
        };
    }

    private Func<TOperations, IEvent, CancellationToken, ValueTask> buildApplication(Type eventType, MethodInfo method)
    {
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TOperations), "ops");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");
        
        var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
        var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
        var strongTypedEvent = Expression.Convert(e, wrappedType);
        var data = Expression.Call(strongTypedEvent, getData);
        
        Func<ParameterInfo, Expression> buildParameter = p =>
        {
            if (p.ParameterType == eventType) return data;
            if (p.ParameterType == wrappedType) return strongTypedEvent;
            if (p.ParameterType == typeof(TOperations)) return session;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;
            if (p.ParameterType == typeof(IEvent)) return e;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };
        
        // You would use null for static methods
        Expression caller = default(Expression);
        if (!method.IsStatic) caller = Expression.Constant(_entity);
            
        var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
        var body = Expression.Call(caller, method, arguments);

        if (body.Type == typeof(ValueTask))
        {
            return Expression
                .Lambda<Func<TOperations, IEvent, CancellationToken, ValueTask>>(body, session, e, cancellation)
                .CompileFast();
        }

        if (body.Type == typeof(Task))
        {
            var func = Expression
                .Lambda<Func<TOperations, IEvent, CancellationToken, Task>>(body, session, e, cancellation)
                .CompileFast();
            return (o1, e1, c1) => new ValueTask(func(o1, e1, c1));
        }
        var apply =  Expression
            .Lambda<Action<TOperations, IEvent>>(body, session, e)
            .CompileFast();

        return (o1, e1, _) =>
        {
            apply(o1, e1);
            return new ValueTask();
        };
    }
    
    private Func<TOperations, IEvent, CancellationToken, ValueTask> buildCreator(Type eventType, MethodInfo method)
    {
        var entityType = method.ReturnType;
        if (entityType.Closes(typeof(Task<>))) entityType = entityType.GetGenericArguments()[0];
        if (entityType.Closes(typeof(ValueTask<>))) entityType = entityType.GetGenericArguments()[0];

        var builder = typeof(CreatorBuilder<>).CloseAndBuildAs<ICreatorBuilder>( entityType);
        return builder.Build<TOperations>(_entity, eventType, method);
    }

    internal class ProjectMethodCollection: MethodCollection
    {
        public static readonly string MethodName = "Project";
        
        public ProjectMethodCollection(Type projectionType): base(MethodName, projectionType, null)
        {
            _validArgumentTypes.Add(typeof(TOperations));
            _validReturnTypes.Add(typeof(void));
            _validReturnTypes.Add(typeof(Task));
        }

        internal override void validateMethod(MethodSlot method)
        {
            if (method.Method.GetParameters().All(x => x.ParameterType != typeof(TOperations)))
            {
                method.AddError($"{typeof(TOperations).FullNameInCode()} is a required parameter");
            }
        }
    }
    
    internal class CreateMethodCollection: MethodCollection
    {
        public static readonly string MethodName = "Create";
        public static readonly string TransformMethodName = "Transform";
        
        public CreateMethodCollection(Type projectionType): base([MethodName, TransformMethodName], projectionType, null)
        {
            _validArgumentTypes.Add(typeof(TOperations));
        }

        internal override void validateMethod(MethodSlot method)
        {
            if (method.ReturnType == typeof(void))
            {
                method.AddError("The return value must be a new document");
            }
        }
    }

    public bool HasAnyMethods()
    {
        return _projectMethods.Methods.Any() || _createMethods.Methods.Any() || !_applications.IsEmpty;
    }

    public void AssertMethodValidity()
    {
        if (!_projectMethods.Methods.Any() && !_createMethods.Methods.Any() && _applications.IsEmpty)
        {
            throw new InvalidProjectionException(
                $"EventProjection {GetType().FullNameInCode()} has no valid projection operations. Either use the Lambda registrations, or expose methods named '{ProjectMethodCollection.MethodName}', '{CreateMethodCollection.MethodName}', or '{CreateMethodCollection.TransformMethodName}'");
        }

        var invalidMethods = MethodCollection.FindInvalidMethods(GetType(), _projectMethods, _createMethods);
        if (invalidMethods.Any())
        {
            throw new InvalidProjectionException(_entity, invalidMethods);
        }
    }

    public void Project<TEvent>(Action<TEvent,TOperations> project) where TEvent : class
    {
        projectEvent<TEvent>(project);
    }

    public void ProjectAsync<TEvent>(Func<TEvent, TOperations, CancellationToken, Task> project) where TEvent : class
    {
        projectEvent<TEvent>(project);
    }
    
    private void projectEvent<TEvent>(object handler) where TEvent : class
    {
        var e = Expression.Parameter(typeof(IEvent), "e");
        var session = Expression.Parameter(typeof(TOperations), "session");
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
            if (p.ParameterType == typeof(TOperations)) return session;
            if (p.ParameterType == typeof(CancellationToken)) return cancellation;

            throw new ArgumentOutOfRangeException(nameof(p),
                $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
        };

        var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
        var body = Expression.Call(caller, method, arguments);

        Func<TOperations, IEvent, CancellationToken, ValueTask> func = default;
        if (body.Type == typeof(ValueTask))
        {
            func = Expression
                .Lambda<Func<TOperations, IEvent, CancellationToken, ValueTask>>(body, session, e, cancellation)
                .CompileFast();

            
        }
        else if (body.Type == typeof(Task))
        {
            var inner = Expression
                .Lambda<Func<TOperations, IEvent, CancellationToken, Task>>(body, session, e, cancellation)
                .CompileFast();
            func = (o1, e1, c1) => new ValueTask(inner(o1, e1, c1));
        }
        else
        {
            var apply =  Expression
                .Lambda<Action<TOperations, IEvent>>(body, session, e)
                .CompileFast();

            func = (o1, e1, _) =>
            {
                apply(o1, e1);
                return new ValueTask();
            };
        }
        
        _applications = _applications.AddOrUpdate(eventType, func);
    }
}

    internal interface ICreatorBuilder
    {
        Func<TOperations, IEvent, CancellationToken, ValueTask> Build<TOperations>(IEntityStorage<TOperations> entityStorage, Type eventType,
            MethodInfo method);
    }

    internal class CreatorBuilder<T> : ICreatorBuilder
    {
        public Func<TOperations, IEvent, CancellationToken, ValueTask> Build<TOperations>(IEntityStorage<TOperations> entityStorage,
            Type eventType, MethodInfo method)
        {
            var e = Expression.Parameter(typeof(IEvent), "e");
            var session = Expression.Parameter(typeof(TOperations), "ops");
            var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellation");
        
            var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);
            var getData = wrappedType.GetProperty(nameof(IEvent.Data)).GetMethod;
            var strongTypedEvent = Expression.Convert(e, wrappedType);
            var data = Expression.Call(strongTypedEvent, getData);
        
            Func<ParameterInfo, Expression> buildParameter = p =>
            {
                if (p.ParameterType == eventType) return data;
                if (p.ParameterType == wrappedType) return strongTypedEvent;
                if (p.ParameterType == typeof(TOperations)) return session;
                if (p.ParameterType == typeof(CancellationToken)) return cancellation;
                if (p.ParameterType == typeof(IEvent)) return e;

                throw new ArgumentOutOfRangeException(nameof(p),
                    $"Unable to determine an expression for parameter {p.Name} of type {p.ParameterType.FullNameInCode()}");
            };
            
            // You would use null for static methods
            Expression caller = default(Expression);
            if (!method.IsStatic) caller = Expression.Constant(entityStorage);
            
            var arguments = method.GetParameters().Select(x => buildParameter(x)).ToArray();
            var body = Expression.Call(caller, method, arguments);

            if (body.Type == typeof(T))
            {
                var func = Expression
                    .Lambda<Func<TOperations, IEvent, CancellationToken, T>>(body, session, e, cancellation)
                    .CompileFast();

                return (o1, e1, c1) =>
                {
                    entityStorage.Store(o1, func(o1, e1, c1));
                    return new ValueTask();
                };
            }

            if (body.Type == typeof(ValueTask<T>))
            {
                var func = Expression
                    .Lambda<Func<TOperations, IEvent, CancellationToken, ValueTask<T>>>(body, session, e, cancellation)
                    .CompileFast();
                
                return async (o1, e1, c1) =>
                {
                    entityStorage.Store(o1, await func(o1, e1, c1));
                };
            }
            
            var taskFunc = Expression
                .Lambda<Func<TOperations, IEvent, CancellationToken, Task<T>>>(body, session, e, cancellation)
                .CompileFast();
                
            return async (o1, e1, c1) =>
            {
                entityStorage.Store(o1, await taskFunc(o1, e1, c1));
            };
        }
    }
    