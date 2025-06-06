using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Util;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Frames;

public enum ConstructorCallMode
{
    Variable,
    ReturnValue,
    UsingNestedVariable
}

public class SetterArg
{
    public SetterArg(string propertyName, Type propertyType)
    {
        PropertyName = propertyName;
        PropertyType = propertyType;
    }

    public SetterArg(PropertyInfo property)
    {
        PropertyName = property.Name;
        PropertyType = property.PropertyType;
    }

    public SetterArg(PropertyInfo property, Variable? variable)
    {
        PropertyName = property.Name;
        PropertyType = property.PropertyType;
        Variable = variable;
    }

    public string PropertyName { get; }
    public Variable? Variable { get; private set; }
    public Type PropertyType { get; }

    public string Assignment()
    {
        return $"{PropertyName} = {Variable!.Usage}";
    }

    [MemberNotNull(nameof(Variable))]
    internal void FindVariable(IMethodVariables chain)
    {
        if (Variable == null)
        {
            Variable = chain.FindVariable(PropertyType);
        }
    }
}

public class ConstructorFrame : SyncFrame
{
    public ConstructorFrame(ConstructorInfo ctor) : this(ctor.DeclaringType!, ctor)
    {
    }

    public ConstructorFrame(Type builtType, ConstructorInfo ctor)
    {
        Ctor = ctor ?? throw new ArgumentNullException(nameof(ctor));
        Parameters = new Variable[ctor.GetParameters().Length];


        BuiltType = builtType;
        Variable = new Variable(BuiltType, this);

        IsAsync = builtType.CanBeCastTo<IAsyncDisposable>();
    }

    public ConstructorFrame(Type builtType, ConstructorInfo ctor, Func<ConstructorFrame, Variable> variableSource)
    {
        Ctor = ctor ?? throw new ArgumentNullException(nameof(ctor),
            $"No usable constructor for type '{builtType.FullNameInCode()}'");
        Parameters = new Variable[ctor.GetParameters().Length];


        BuiltType = builtType;
        Variable = variableSource(this);

        IsAsync = builtType.CanBeCastTo<IAsyncDisposable>();
    }

    /// <summary>
    ///     <summary>
    ///         Optional code fragment to write at the beginning of this
    ///         type in code
    ///     </summary>
    public ICodeFragment? Header { get; set; }


    public Type BuiltType { get; }

    public Type? DeclaredType { get; set; }

    public ConstructorInfo Ctor { get; }

    public Variable[] Parameters { get; set; }

    public FramesCollection ActivatorFrames { get; } = new(null!);

    public ConstructorCallMode Mode { get; set; } = ConstructorCallMode.Variable;

    public IList<SetterArg> Setters { get; } = new List<SetterArg>();

    /// <summary>
    ///     The variable set by invoking this frame.
    /// </summary>
    public Variable Variable { get; protected set; }

    /// <summary>
    ///     Add a single line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void Comment(string text)
    {
        Header = new OneLineComment(text);
    }

    /// <summary>
    ///     Add a multi line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void MultiLineComment(string text)
    {
        Header = new MultiLineComment(text);
    }


    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Header != null)
        {
            writer.WriteLine("");
            Header.Write(writer);
        }

        switch (Mode)
        {
            case ConstructorCallMode.Variable:
                writer.Write(Declaration() + ";");
                ActivatorFrames.Write(method, writer);

                Next?.GenerateCode(method, writer);
                break;

            case ConstructorCallMode.ReturnValue:
                if (ActivatorFrames.Any())
                {
                    writer.Write(Declaration() + ";");
                    ActivatorFrames.Write(method, writer);

                    writer.Write($"return {Variable.Usage};");
                    Next?.GenerateCode(method, writer);
                }
                else
                {
                    writer.Write($"return {Invocation()};");
                    Next?.GenerateCode(method, writer);
                }

                break;

            case ConstructorCallMode.UsingNestedVariable:
                if (BuiltType.CanBeCastTo<IAsyncDisposable>())
                {
                    writer.WriteLine($"await using {Declaration()};");
                }
                else
                {
                    writer.WriteLine($"using {Declaration()};");
                }

                ActivatorFrames.Write(method, writer);
                Next?.GenerateCode(method, writer);
                break;
        }
    }


    public string Declaration()
    {
        return DeclaredType == null
            ? $"{Variable.AssignmentUsage} = {Invocation()}"
            : $"{DeclaredType.FullNameInCode()} {Variable.Usage} = {Invocation()}";
    }

    public string Invocation()
    {
        var invocation = $"new {BuiltType.FullNameInCode()}({Parameters.Select(x => x.Usage).Join(", ")})";
        if (Setters.Any())
        {
            invocation += $"{{{Setters.Select(x => x.Assignment()).Join(", ")}}}";
        }

        return invocation;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        var parameters = Ctor.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (Parameters[i] == null)
            {
                var parameter = parameters[i];
                Parameters[i] = chain.FindVariable(parameter.ParameterType);
            }
        }

        foreach (var parameter in Parameters) yield return parameter;

        foreach (var setter in Setters) setter.FindVariable(chain);

        foreach (var setter in Setters) yield return setter.Variable!;


        if (ActivatorFrames.Any())
        {
            var standin = new StandinMethodVariables(Variable, chain);

            foreach (var frame in ActivatorFrames)
            foreach (var variable in frame.FindVariables(standin))
                yield return variable;
        }
    }


    public class StandinMethodVariables : IMethodVariables
    {
        private readonly Variable _current;
        private readonly IMethodVariables _inner;

        public StandinMethodVariables(Variable current, IMethodVariables inner)
        {
            _current = current;
            _inner = inner;
        }

        public Variable FindVariable(Type type)
        {
            return type == _current.VariableType ? _current : _inner.FindVariable(type);
        }

        public Variable FindVariable(ParameterInfo parameter)
        {
            return _inner.FindVariable(parameter);
        }

        public Variable FindVariableByName(Type dependency, string name)
        {
            return _inner.FindVariableByName(dependency, name);
        }

        public bool TryFindVariableByName(Type dependency, string name, [NotNullWhen(true)] out Variable? variable)
        {
            return _inner.TryFindVariableByName(dependency, name, out variable);
        }

        public Variable? TryFindVariable(Type type, VariableSource source)
        {
            return _inner.TryFindVariable(type, source);
        }
    }
}

public class ConstructorFrame<T> : ConstructorFrame
{
    public ConstructorFrame(ConstructorInfo ctor) : base(typeof(T), ctor)
    {
    }

    public ConstructorFrame(Expression<Func<T>> expression) : base(typeof(T),
        ConstructorFinderVisitor<T>.Find(expression))
    {
    }

    public void Set(Expression<Func<T, object>> expression, Variable? variable = null)
    {
        var property = ReflectionHelper.GetProperty(expression);
        var setter = new SetterArg(property, variable);

        Setters.Add(setter);
    }
}