using System.Linq.Expressions;

namespace JasperFx.Core.Reflection;

public static class ExpressionExtensions
{
    // Meant to wrap Task, then return the aggregate
    public static async ValueTask<T> WrapTask<T>(Task task, T returnValue)
    {
        await task;
        return returnValue;
    }
    
    // Meant to wrap ValueTask, then return the aggregate
    public static async ValueTask<T> WrapValueTask<T>(ValueTask task, T returnValue)
    {
        await task;
        return returnValue;
    }
    
    public static Expression MaybeWrapWithValueTask<T>(this Expression expression)
    {
        var valueTaskType = typeof(ValueTask<T>);

        // If it's already good, get out of here
        if (expression.Type == valueTaskType) return expression;

        // Wrap it if it's just the value
        if (expression.Type == typeof(T))
        {
            var ctor = valueTaskType.GetConstructor([typeof(T)])!;
            return Expression.New(ctor, expression);
        }

        var taskType = typeof(Task<T>);
        if (expression.Type == taskType)
        {
            var ctor = valueTaskType.GetConstructor([taskType])!;
            return Expression.New(ctor, expression);
        }

        throw new ArgumentOutOfRangeException(nameof(expression),
            $"No known way to convert type {expression.Type} to {valueTaskType.ShortNameInCode()}");
    }

    public static Expression ReturnWithValueTask<T>(this Expression expression, Expression argument)
    {
        var valueTaskType = typeof(ValueTask<T>);

        // If it's already good, get out of here
        if (expression.Type == valueTaskType) return expression;

        // Wrap it if it's just the value
        if (expression.Type == typeof(T))
        {
            var ctor = valueTaskType.GetConstructor([typeof(T)])!;
            return Expression.New(ctor, expression);
        }
        
        if (expression.Type == typeof(void))
        {
            return Expression.Block(expression, argument.MaybeWrapWithValueTask<T>());
        }

        var taskType = typeof(Task<T>);
        if (expression.Type == taskType)
        {
            var ctor = valueTaskType.GetConstructor([taskType])!;
            return Expression.New(ctor, expression);
        }

        if (expression.Type == typeof(Task))
        {
            var method = typeof(ExpressionExtensions).GetMethod(nameof(ExpressionExtensions.WrapTask))!.MakeGenericMethod(typeof(T));
            return Expression.Call(null, method, expression, argument);
        }
        
        if (expression.Type == typeof(ValueTask))
        {
            var method = typeof(ExpressionExtensions).GetMethod(nameof(ExpressionExtensions.WrapValueTask))!.MakeGenericMethod(typeof(T));
            return Expression.Call(null, method, expression, argument);
        }
        
        throw new ArgumentOutOfRangeException(nameof(expression),
            $"No known way to convert type {expression.Type} to {valueTaskType.ShortNameInCode()}");
    }
}