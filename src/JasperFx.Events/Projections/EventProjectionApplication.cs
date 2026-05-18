using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;

namespace JasperFx.Events.Projections;

public interface IEntityStorage<TOperations>
{
    void Store<T>(TOperations ops, T entity) where T : notnull;
}

[UnconditionalSuppressMessage("Trimming", "IL2062:DynamicallyAccessedMembers",
    Justification = "Class-level: passes event-type Type values resolved reflectively to TypeExtensions.Closes(...). Event types preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level: assigns reflective Type/MethodInfo results to DAM-annotated targets when validating projection methods. Source types preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level: PublicMethods/PublicProperties access via Type returned by other reflection calls. Source preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic method parameter receives Type values obtained reflectively (eventType). Event types preserved at registration.")]
public class EventProjectionApplication<TOperations>
{
    private readonly IEntityStorage<TOperations> _entity;
    private readonly ProjectMethodCollection _projectMethods;
    private readonly CreateMethodCollection _createMethods;
    private readonly Type _projectionType;

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
            .Distinct().ToArray();
    }

    /// <summary>
    /// Backstop runtime dispatch. When the source generator emits an override on the
    /// user's partial projection class, that override wins at virtual dispatch and this
    /// body is never reached. If we get here, the projection was registered without a
    /// source-generated dispatcher and the registration-time fail-fast either didn't
    /// run or was bypassed.
    /// </summary>
    public ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken token)
    {
        throw new InvalidOperationException(MissingDispatcherMessage());
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

    internal class ProjectMethodCollection : MethodCollection
    {
        public static readonly string MethodName = "Project";

        public ProjectMethodCollection(Type projectionType) : base(MethodName, projectionType, null)
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

    internal class CreateMethodCollection : MethodCollection
    {
        public static readonly string MethodName = "Create";
        public static readonly string TransformMethodName = "Transform";

        public CreateMethodCollection(Type projectionType) : base([MethodName, TransformMethodName], projectionType, null)
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
        return _projectMethods.Methods.Any() || _createMethods.Methods.Any();
    }

    public void AssertMethodValidity()
    {
        if (!_projectMethods.Methods.Any() && !_createMethods.Methods.Any())
        {
            throw new InvalidProjectionException(
                $"EventProjection {_projectionType.FullNameInCode()} has no valid projection operations. " +
                $"Expose methods named '{ProjectMethodCollection.MethodName}', " +
                $"'{CreateMethodCollection.MethodName}', or '{CreateMethodCollection.TransformMethodName}'.");
        }

        var invalidMethods = MethodCollection.FindInvalidMethods(_projectionType, _projectMethods, _createMethods);
        if (invalidMethods.Any())
        {
            throw new InvalidProjectionException(_entity, invalidMethods);
        }
    }

    internal string MissingDispatcherMessage()
    {
        return $"No source-generated dispatcher found for EventProjection {_projectionType.FullNameInCode()}. " +
               "When using conventional Project/Create/Transform methods, the projection class must be declared " +
               "`partial` in an assembly that references the JasperFx.Events.SourceGenerator analyzer, " +
               "or alternatively override ApplyAsync directly. " +
               "See docs/codegen/aot.md for the AOT publishing guide.";
    }
}
