using System.Linq.Expressions;
using System.Reflection;

namespace JasperFx.Core.Reflection
{
    /// <summary>
    /// Can be used to create compiled Func's to retrieve a value from the TTarget type
    /// </summary>
    public static class LambdaBuilder
    {
        public static Func<TTarget, TProperty> GetProperty<TTarget, TProperty>(PropertyInfo property)
        {
            var target = Expression.Parameter(property.DeclaringType!, "target");
            var method = property.GetGetMethod()!;

            var callGetMethod = Expression.Call(target, method);

            var lambda = method.ReturnType == typeof(TProperty)
                ? Expression.Lambda<Func<TTarget, TProperty>>(callGetMethod, target)
                : Expression.Lambda<Func<TTarget, TProperty>>(Expression.Convert(callGetMethod, typeof(TProperty)),
                    target);

            return lambda.Compile();
        }

        /// <summary>
        /// Create an Action to set a property on the type TTarget
        /// </summary>
        /// <param name="property"></param>
        /// <typeparam name="TTarget"></typeparam>
        /// <typeparam name="TProperty"></typeparam>
        /// <returns></returns>
        public static Action<TTarget, TProperty>? SetProperty<TTarget, TProperty>(PropertyInfo property)
        {
            var target = Expression.Parameter(typeof(TTarget), "target");
            var value = Expression.Parameter(typeof(TProperty), "value");

            var method = property.SetMethod;

            if (method == null) return null;
            
            Expression actualValue = property.PropertyType == typeof(TProperty) ? value : Expression.Convert(value, property.PropertyType);

            var callSetMethod = Expression.Call(target, method, actualValue);

            var lambda = Expression.Lambda<Action<TTarget, TProperty>>(callSetMethod, target, value);

            return lambda.Compile();
        }


        public static Func<TTarget, TField> GetField<TTarget, TField>(FieldInfo field)
        {
            var target = Expression.Parameter(typeof(TTarget), "target");

            var fieldAccess = Expression.Field(target, field);

            var lambda = field.FieldType == typeof(TField)
                ? Expression.Lambda<Func<TTarget, TField>>(fieldAccess, target)
                : Expression.Lambda<Func<TTarget, TField>>(Expression.Convert(fieldAccess, typeof(TField)), target);

            return lambda.Compile();
        }

        public static Func<TTarget, TMember> Getter<TTarget, TMember>(MemberInfo member)
        {
            return member is PropertyInfo
                ? GetProperty<TTarget, TMember>(member.As<PropertyInfo>())
                : GetField<TTarget, TMember>(member.As<FieldInfo>());
        }


        public static Action<TTarget, TField> SetField<TTarget, TField>(FieldInfo field)
        {
            var target = Expression.Parameter(typeof(TTarget), "target");
            var value = Expression.Parameter(typeof(TField), "value");

            var fieldAccess = Expression.Field(target, field);
            var fieldSetter = Expression.Assign(fieldAccess, value);

            var lambda = Expression.Lambda<Action<TTarget, TField>>(fieldSetter, target, value);

            return lambda.Compile();
        }


        public static Action<TTarget, TMember>? Setter<TTarget, TMember>(MemberInfo member)
        {
            return member is PropertyInfo
                ? SetProperty<TTarget, TMember>(member.As<PropertyInfo>())
                : SetField<TTarget, TMember>(member.As<FieldInfo>());
        }



    }

}