using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

public interface IAggregateVersioning
{
    public MemberInfo? VersionMember
    {
        get;
    }

    void TrySetVersion(object aggregate, IEvent lastEvent);
    long GetVersion(object aggregate);
}

public interface IAggregateVersioning<in T>
{
    void TrySetVersion(T aggregate, IEvent lastEvent);
}

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: routes the Version member setter through LambdaBuilder, which is RequiresUnreferencedCode under the hood (FastExpressionCompiler-compiled expression tree). The aggregate type T is preserved by the registered projection boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument flow on the aggregator. T preserved by registration.")]
public class AggregateVersioning<T> : IAggregateVersioning
{
    private readonly Lazy<Action<T, IEvent>> _setValue;
    private readonly AggregationScope _scope;
    
    public AggregateVersioning(AggregationScope scope)
    {
        _scope = scope;
        _setValue = new Lazy<Action<T, IEvent>>(buildAction);
        
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var props = typeof(T).GetProperties(bindingFlags)
            .Where(x => x.CanWrite)
            .Where(x => x.PropertyType == typeof(int) || x.PropertyType == typeof(long))
            .OfType<MemberInfo>();

        var fields = typeof(T).GetFields(bindingFlags)
            .Where(x => x.FieldType == typeof(int) || x.FieldType == typeof(long))
            .OfType<MemberInfo>();

        var members = props.Concat(fields);
        // ReSharper disable once PossibleMultipleEnumeration
        VersionMember = members.FirstOrDefault(x => x.GetCustomAttributes().OfType<IVersionAttribute>().Any());
        // ReSharper disable once PossibleMultipleEnumeration
        VersionMember ??= members.FirstOrDefault(x =>
            x.Name.EqualsIgnoreCase("version") && !x.HasAttribute<JasperFxIgnoreAttribute>());
    }
    
    public MemberInfo? VersionMember
    {
        get;
        private set;
    }
    
    long IAggregateVersioning.GetVersion(object aggregate)
    {
        return GetVersion((T)aggregate);
    }
    
    void IAggregateVersioning.TrySetVersion(object? aggregate, IEvent? lastEvent)
    {
        if (aggregate == null || lastEvent == null)
        {
            return;
        }

        TrySetVersion((T)aggregate, lastEvent);
    }
    
    public void TrySetVersion(T? aggregate, IEvent? lastEvent)
    {
        if (aggregate == null || lastEvent == null)
        {
            return;
        }

        _setValue.Value(aggregate, lastEvent);
    }
    
    private Action<T, IEvent> buildAction()
    {
        if (VersionMember == null)
        {
            return (_, _) => { };
        }

        var memberType = VersionMember.GetMemberType();
        var singleStream = _scope == AggregationScope.SingleStream;

        if (memberType == typeof(int))
        {
            var setter = LambdaBuilder.Setter<T, int>(VersionMember)
                         ?? throw new InvalidOperationException(
                             $"Unable to build a setter for the Version member '{VersionMember.Name}' on {typeof(T).FullNameInCode()}");

            return singleStream
                ? (aggregate, e) => setter(aggregate, Convert.ToInt32(e.Version))
                : (aggregate, e) => setter(aggregate, Convert.ToInt32(e.Sequence));
        }

        if (memberType == typeof(long))
        {
            var setter = LambdaBuilder.Setter<T, long>(VersionMember)
                         ?? throw new InvalidOperationException(
                             $"Unable to build a setter for the Version member '{VersionMember.Name}' on {typeof(T).FullNameInCode()}");

            return singleStream
                ? (aggregate, e) => setter(aggregate, e.Version)
                : (aggregate, e) => setter(aggregate, e.Sequence);
        }

        throw new InvalidOperationException(
            $"The Version member '{VersionMember.Name}' on {typeof(T).FullNameInCode()} must be an int or long");
    }
    
    public void Override(Expression<Func<T, int>> expression)
    {
        VersionMember = ReflectionHelper.GetProperty(expression);
        if (VersionMember == null) throw new ArgumentOutOfRangeException(nameof(expression), "Unable to find a property in the supplied expression. Must be directly on the aggregate type");
    }

    public void Override(Expression<Func<T, long>> expression)
    {
        VersionMember = ReflectionHelper.GetProperty(expression);
        if (VersionMember == null) throw new ArgumentOutOfRangeException(nameof(expression), "Unable to find a property in the supplied expression. Must be directly on the aggregate type");
    }

    public long GetVersion(T aggregate)
    {
        if (VersionMember is PropertyInfo prop)
        {
            return Convert.ToInt64(prop.GetValue(aggregate));
        }

        return Convert.ToInt64(VersionMember!.As<FieldInfo>().GetValue(aggregate));
    }
}

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: same as the single-arg sibling — Version member setter via LambdaBuilder (RUC). T preserved by registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument flow on the aggregator. T preserved by registration.")]
public class AggregateVersioning<T, TQuerySession> : AggregateVersioning<T>, IAggregateVersioning, IAggregateVersioning<T>, IAggregator<T, TQuerySession>
{
    public AggregateVersioning(AggregationScope scope) : base(scope)
    {
        
    }

    public Type IdentityType => Inner.IdentityType;

    public IAggregator<T, TQuerySession> Inner { get; init; } = null!;
    
    public async ValueTask<T?> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, T? snapshot,
        CancellationToken cancellation)
    {
        var aggregate = await Inner.BuildAsync(events, session, snapshot, cancellation).ConfigureAwait(false);
        TrySetVersion(aggregate, events.LastOrDefault());
        return aggregate;
    }
}
