using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

public static class ValueTypeInfoExtensions
{
    public static Func<IEvent, TId> CreateAggregateIdentitySource<TId>(this ValueTypeInfo valueTypeInfo)
        where TId : notnull
    {
        var e = Expression.Parameter(typeof(IEvent), "e");
        var eMember = valueTypeInfo.SimpleType == typeof(Guid)
            ? ReflectionHelper.GetProperty<IEvent>(x => x.StreamId)
            : ReflectionHelper.GetProperty<IEvent>(x => x.StreamKey);

        var raw = Expression.Call(e, eMember.GetMethod);
        Expression wrapped = null;
        if (valueTypeInfo.Builder != null)
        {
            wrapped = Expression.Call(null, valueTypeInfo.Builder, raw);
        }
        else if (valueTypeInfo.Ctor != null)
        {
            wrapped = Expression.New(valueTypeInfo.Ctor, raw);
        }
        else
        {
            throw new NotSupportedException("Marten cannot build a type converter for strong typed id type " +
                                            valueTypeInfo.OuterType.FullNameInCode());
        }

        var lambda = Expression.Lambda<Func<IEvent, TId>>(wrapped, e);

        return lambda.CompileFast();
    }
}
