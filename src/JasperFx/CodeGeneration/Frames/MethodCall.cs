using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;

namespace JasperFx.CodeGeneration.Frames;

public enum DisposalMode
{
    UsingBlock,
    None
}

public enum ReturnAction
{
    /// <summary>
    ///     The value built by the MethodCall should be like 'return Method()'
    /// </summary>
    Return,

    /// <summary>
    ///     The value built by the MethodCall should be assigned to the ReturnVariable
    ///     like 'x = Method();'
    /// </summary>
    Assign,

    /// <summary>
    ///     The value built by the MethodCall should be assigned to the ReturnVariable
    ///     like 'var x = Method();`
    /// </summary>
    Initialize
}

public class MethodCall : Frame
{
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("MethodCall reflects over handlerType.GetMethod(methodName); the requested method must survive trimming.")]
    public MethodCall(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods)] Type handlerType,
        string methodName) : this(handlerType, handlerType.GetMethod(methodName)!)
    {
    }


    public MethodCall(Type handlerType, MethodInfo method) : base(method.IsAsync())
    {
        HandlerType = handlerType;
        Method = method;

        ReturnType = correctedReturnType(method.ReturnType);
        if (ReturnType != null)
        {
            if (ReturnType.IsValueTuple())
            {
                var values = buildTupleCreateVariables().ToArray();

                ReturnVariable = new ValueTypeReturnVariable(ReturnType, values);
            }
            else
            {
                var name = ReturnType.IsSimple() || ReturnType == typeof(object) || ReturnType == typeof(object[])
                    ? "result_of_" + method.Name
                    : Variable.DefaultArgName(ReturnType);


                ReturnVariable = new Variable(ReturnType, name, this);
            }
        }


        var parameters = method.GetParameters();
        Arguments = new Variable[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.IsOut)
            {
                var paramType = param.ParameterType.IsByRef
                    ? param.ParameterType.GetElementType()
                    : param.ParameterType;
                Arguments[i] = new OutArgument(paramType!, this);
            }
        }
    }

    [IgnoreDescription]
    public Dictionary<Type, Type> Aliases { get; } = new();

    public Type HandlerType { get; }
    
    [IgnoreDescription]
    public MethodInfo Method { get; }

    public string MethodSignature => $"{Method.Name}({Method.GetParameters().Select(x => x.ParameterType.ShortNameInCode()).Join(", ")})";

    [IgnoreDescription]
    public Variable? ReturnVariable { get; private set; }

    public Type? ReturnType { get; }

    /// <summary>
    ///     Optional text to write as a descriptive comment
    ///     in the generated code
    /// </summary>
    public string? CommentText { get; set; }


    /// <summary>
    ///     Call a method on the current object
    /// </summary>
    public bool IsLocal { get; set; }

    [IgnoreDescription]
    public Variable? Target { get; set; }

    [IgnoreDescription]
    public Variable[] Arguments { get; }

    public DisposalMode DisposalMode { get; set; } = DisposalMode.UsingBlock;

    /// <summary>
    ///     How should the ReturnVariable handled within the generated code? Initialize is the default.
    /// </summary>
    public ReturnAction ReturnAction { get; set; } = ReturnAction.Initialize;

    /// <summary>
    /// Optional name of an <see cref="ActivityEvent"/> to append to the
    /// current <see cref="Activity"/> immediately BEFORE this method is invoked.
    /// When set, <see cref="GenerateCode"/> emits the equivalent of
    /// <c>Activity.Current?.AddEvent(new ActivityEvent("..."))</c> using fully
    /// qualified type names. No-op when the value is null or empty.
    /// </summary>
    public string? ActivityEventBeforeCall { get; set; }

    /// <summary>
    /// Optional name of an <see cref="ActivityEvent"/> to append to the
    /// current <see cref="Activity"/> immediately AFTER this method is invoked
    /// (success path; events are not emitted from a catch). When set,
    /// <see cref="GenerateCode"/> emits the equivalent of
    /// <c>Activity.Current?.AddEvent(new ActivityEvent("..."))</c> using fully
    /// qualified type names. No-op when the value is null or empty.
    /// </summary>
    public string? ActivityEventAfterCall { get; set; }
    
    private IEnumerable<Variable> buildTupleCreateVariables()
    {
        foreach (var type in ReturnType!.GetGenericArguments())
        {
            var count = Creates.Count(x => x.VariableType == type);
            var suffix = count > 0 ? (count + 1).ToString() : string.Empty;
            var created = new Variable(type, Variable.DefaultArgName(type) + suffix, this);

            yield return created;
        }
    }

    /// <summary>
    ///     Does this MethodCall create a new variable of the object type? This includes the return variable, destructuring
    ///     tuples, or out parameters
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool CreatesNewOf<T>()
    {
        return CreatesNewOf(typeof(T));
    }

    /// <summary>
    ///     Does this MethodCall create a new variable of the object type? This includes the return variable, destructuring
    ///     tuples, or out parameters
    /// </summary>
    /// <param name="objectType"></param>
    /// <returns></returns>
    public bool CreatesNewOf(Type objectType)
    {
        return ReturnVariable?.VariableType == objectType || Creates.Any(x => x.VariableType == objectType);
    }

    public void AssignResultTo(Variable variable)
    {
        ReturnVariable = variable;
        ReturnAction = ReturnAction.Assign;

        // F#: reassigning a binding requires it to have been declared `let mutable`. Marking the
        // variable here lets its first binding render `let mutable` and this site render `x <- ...`.
        // No effect on the C# path, and no effect for arguments/injected fields (which are never
        // rendered through FSharpAssignmentUsage).
        variable.Mutable = true;
    }

    public static MethodCall For<T>(Expression<Action<T>> expression)
    {
        var method = ReflectionHelper.GetMethod(expression);

        return new MethodCall(typeof(T), method!);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "Checks the return type against well-known framework Task / ValueTask shapes. type.Closes walks well-known interface hierarchies; user-defined task-like types satisfying this are themselves trim-preserved by their consuming context.")]
    private Type? correctedReturnType(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask) || type == typeof(void))
        {
            return null;
        }

        if (type.Closes(typeof(ValueTask<>)))
        {
            return type.GetGenericArguments().First();
        }

        if (type.CanBeCastTo<Task>())
        {
            var inner = type.GetGenericArguments().First();
            if (inner.FullNameInCode() == "Microsoft.FSharp.Core.Unit")
            {
                return null;
            }

            return inner;
        }

        // Let the F# folks cook!
        if (type.FullNameInCode() == "Microsoft.FSharp.Core.Unit") return null;

        return type;
    }


    private Variable findVariable(ParameterInfo param, IMethodVariables chain)
    {
        var type = param.ParameterType;

        if (Aliases.TryGetValue(type, out var actualType))
        {
            var inner = chain.FindVariable(actualType);
            return new CastVariable(inner, type);
        }

        return chain.FindVariable(param);
    }

    public bool TrySetArgument(Variable variable)
    {
        var parameters = Method.GetParameters().Select(x => x.ParameterType).ToArray();
        if (parameters.Count(x => variable.VariableType == x) != 1)
        {
            return false;
        }

        var index = Array.IndexOf(parameters, variable.VariableType);
        Arguments[index] = variable;

        return true;
    }

    public bool TrySetArgument(string parameterName, Variable variable)
    {
        var parameters = Method.GetParameters();
        var matching = parameters.FirstOrDefault(x =>
            variable.VariableType == x.ParameterType && x.Name == parameterName);

        if (matching == null)
        {
            return false;
        }

        var index = Array.IndexOf(parameters, matching);
        Arguments[index] = variable;

        return true;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        var parameters = Method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract Arguments might be nullable here but I don't want to touch the type and the warning isn't useful.
            if (Arguments[i] != null)
            {
                continue;
            }

            var param = parameters[i];
            Arguments[i] = findVariable(param, chain);
        }

        foreach (var variable in Arguments) yield return variable;

        if (Method.IsStatic || IsLocal)
        {
            yield break;
        }

        if (Target == null)
        {
            Target = chain.FindVariable(HandlerType);
        }

        yield return Target;
    }


    private string returnActionCode(GeneratedMethod method)
    {
        if (IsAsync && method.AsyncMode == AsyncMode.ReturnFromLastNode)
        {
            return "return ";
        }

        if (ReturnVariable == null)
        {
            return string.Empty;
        }

        if (ReturnVariable.VariableType.IsValueTuple())
        {
            return $"{ReturnVariable.Usage} = ";
        }


        switch (ReturnAction)
        {
            case ReturnAction.Initialize:
                return $"var {ReturnVariable.Usage} = ";
            case ReturnAction.Assign:
                return $"{ReturnVariable.Usage} = ";
            case ReturnAction.Return:
                return "return ";
        }

        throw new ArgumentOutOfRangeException();
    }

    private bool shouldWriteInUsingBlock(GeneratedMethod method)
    {
        if (ReturnVariable == null)
        {
            return false;
        }

        if (IsAsync && method.AsyncMode == AsyncMode.ReturnFromLastNode)
        {
            return false;
        }

        return (ReturnVariable.VariableType.CanBeCastTo<IDisposable>() ||
                ReturnVariable.VariableType.CanBeCastTo<IAsyncDisposable>()) && DisposalMode == DisposalMode.UsingBlock;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (CommentText.IsNotEmpty())
        {
            writer.WriteLine("");
            writer.WriteComment(CommentText);
        }

        if (ActivityEventBeforeCall.IsNotEmpty())
        {
            writeActivityEvent(writer, ActivityEventBeforeCall);
        }

        var invokeMethod = InvocationCode(method);

        if (shouldWriteInUsingBlock(method))
        {
            var usingPrefix = ReturnType.CanBeCastTo<IAsyncDisposable>() && method.AsyncMode != AsyncMode.None
                ? "await using"
                : "using";

            writer.Write($"{usingPrefix} {returnActionCode(method)}{invokeMethod};");
        }
        else
        {
            writer.Write($"{returnActionCode(method)}{invokeMethod};");
        }

        if (ActivityEventAfterCall.IsNotEmpty())
        {
            writeActivityEvent(writer, ActivityEventAfterCall);
        }

        // This is just to make the generated code a little
        // easier to read
        if (CommentText.IsNotEmpty())
        {
            writer.BlankLine();
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (CommentText.IsNotEmpty())
        {
            writer.WriteLine("");
            writer.WriteComment(CommentText);
        }

        // Activity events use C#-specific object-initializer syntax; they are
        // intentionally not emitted on the F# path for milestone 1.

        writer.Write($"{fsharpReturnActionCode(method)}{fsharpInvocationCode()}");

        if (CommentText.IsNotEmpty())
        {
            writer.BlankLine();
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    private string fsharpInvocationCode()
    {
        var methodName = Method.Name;
        if (Method.IsGenericMethod)
        {
            methodName += $"<{Method.GetGenericArguments().Select(x => x.FSharpName()).Join(", ")}>";
        }

        var callingCode = $"{methodName}({Arguments.Select(x => x.FSharpUsage).Join(", ")})";

        return $"{fsharpDetermineTarget()}{callingCode}";
    }

    private string fsharpDetermineTarget()
    {
        if (IsLocal)
        {
            // A local call targets a method on the generated type itself. C# relies on an implicit
            // `this`; F# has none, so qualify with the named self identifier emitted by
            // GeneratedMethod.WriteFSharpMethod. See jasperfx#393.
            return "this.";
        }

        var target = Method.IsStatic
            ? HandlerType.FSharpName()
            : Target!.FSharpUsage;

        return target + ".";
    }

    private string fsharpReturnActionCode(GeneratedMethod method)
    {
        // A `task { }` computation expression is emitted only for AsyncMode.AsyncTask; everything
        // else (None, ReturnFromLastNode) is a bare F# expression body.
        var insideTaskBlock = method.AsyncMode == AsyncMode.AsyncTask;

        // The last async node IS the method's return value, emitted directly as the trailing Task
        // expression (no task block, no `return!`). F# returns the Task without a state machine.
        if (IsAsync && method.AsyncMode == AsyncMode.ReturnFromLastNode)
        {
            return string.Empty;
        }

        if (ReturnVariable == null)
        {
            // A void async call must still be awaited inside the task block.
            return IsAsync && insideTaskBlock ? "do! " : string.Empty;
        }

        if (ReturnVariable.VariableType.IsValueTuple())
        {
            throw new NotSupportedException(
                "F# code generation does not yet support value-tuple return variables.");
        }

        var awaited = IsAsync && insideTaskBlock;

        switch (ReturnAction)
        {
            case ReturnAction.Initialize:
                // `let x =` (sync) or `let! x =` (await inside the task block)
                return awaited ? $"let! {ReturnVariable.Usage} = " : $"{ReturnVariable.FSharpAssignmentUsage} = ";
            case ReturnAction.Assign:
                // F# reassignment of a `let mutable` binding uses the `<-` operator.
                return awaited ? $"let! {ReturnVariable.Usage} = " : $"{ReturnVariable.Usage} <- ";
            case ReturnAction.Return:
                // Synchronous: the invocation IS the trailing expression (no `return`).
                return insideTaskBlock ? "return! " : string.Empty;
        }

        throw new ArgumentOutOfRangeException();
    }

    private static void writeActivityEvent(ISourceWriter writer, string eventName)
    {
        writer.Write(
            $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.AddEvent(new {typeof(ActivityEvent).FullNameInCode()}(\"{eventName}\"));");
    }

    private string invocationCode()
    {
        var methodName = Method.Name;
        if (Method.IsGenericMethod)
        {
            methodName += $"<{Method.GetGenericArguments().Select(x => x.FullNameInCode()).Join(", ")}>";
        }

        var callingCode = $"{methodName}({Arguments.Select(x => x.ArgumentDeclaration).Join(", ")})";


        var target = determineTarget();
        var invokeMethod = $"{target}{callingCode}";

        return invokeMethod;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
        Justification = "Checks Method.ReturnType against the well-known framework ValueTask / ValueTask<T> shapes. Trim-impact is bounded by framework types preserved by the runtime.")]
    private bool returnsValueTask()
    {
        return Method.ReturnType == typeof(ValueTask) || Method.ReturnType.Closes(typeof(ValueTask<>));
    }

    /// <summary>
    ///     Code to invoke the method without any assignment to a variable
    /// </summary>
    /// <returns></returns>
    public string InvocationCode(GeneratedMethod method)
    {
        var code = invocationCode();
        if (!IsAsync)
        {
            return code;
        }

        if (returnsValueTask())
        {
            if (method.AsyncMode == AsyncMode.ReturnFromLastNode)
            {
                code = $"{code}.{nameof(ValueTask.AsTask)}()";
            }
            else
            {
                code = $"await {code}";
            }
        }
        else if (method.AsyncMode != AsyncMode.ReturnFromLastNode)
        {
            code = $"await {code}.ConfigureAwait(false)";
        }

        return code;
    }

    /// <summary>
    ///     Code to invoke the method and set a variable to the returned value
    /// </summary>
    /// <returns></returns>
    public string AssignmentCode(GeneratedMethod method)
    {
        if (ReturnVariable == null)
        {
            throw new InvalidOperationException($"Method {this} does not have a return value");
        }

        return IsAsync
            ? $"var {ReturnVariable.Usage} = await {InvocationCode(method)}"
            : $"var {ReturnVariable.Usage} = {InvocationCode(method)}";
    }

    private string determineTarget()
    {
        if (IsLocal)
        {
            return string.Empty;
        }

        var target = Method.IsStatic
            ? HandlerType.FullNameInCode()
            : Target!.Usage;

        return target + ".";
    }


    public override bool CanReturnTask()
    {
        return IsAsync;
    }

    public override string ToString()
    {
        return $"{nameof(HandlerType)}: {HandlerType}, {nameof(Method)}: {Method}";
    }

    /// <summary>
    ///     Assign the result of the supplied index within a value tuple return variable
    /// </summary>
    /// <param name="index"></param>
    /// <param name="variable"></param>
    public void AssignResultTo(int index, Variable variable)
    {
        if (ReturnVariable is ValueTypeReturnVariable tuple)
        {
            var inner = tuple.Inners[index].Inner;
            creates.Remove(inner);

            tuple.AssignResultTo(index, variable);
        }
        else
        {
            throw new InvalidOperationException("Return variable is not a tuple");
        }
    }

    public void TryReplaceVariableCreationWithAssignment(Variable variable)
    {
        if (ReturnVariable == null)
        {
            return;
        }

        if (ReturnVariable.VariableType == variable.VariableType)
        {
            AssignResultTo(variable);
        }
        else if (ReturnVariable is ValueTypeReturnVariable tuple)
        {
            var index = -1;
            for (int i = 0; i < tuple.Inners.Length; i++)
            {
                var v = tuple.Inners[i];
                if (v.Inner.VariableType == variable.VariableType)
                {
                    index = i;
                    break;
                }
            }
            
            if (index > -1)
            {
                AssignResultTo(index, variable);
            }
        }
    }
}