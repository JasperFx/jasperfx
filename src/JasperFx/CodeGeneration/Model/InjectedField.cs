using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Model;

public class InjectedField : Variable
{
    public InjectedField(Type argType) : this(argType, DefaultArgName(argType))
    {
    }

    public InjectedField(Type argType, string name) : base(argType, "_" + name)
    {
        CtorArg = name;
        ArgType = argType;
    }

    public Type ArgType { get; }

    public string CtorArg { get; protected set; }

    public virtual string CtorArgDeclaration => $"{ArgType.FullNameInCode()} {CtorArg}";

    public override void OverrideName(string variableName)
    {
        base.OverrideName(variableName);
        CtorArg = $"_{variableName}"; // Gotta do this to disambiguate injected fields!
    }

    public void WriteDeclaration(ISourceWriter writer)
    {
        writer.Write($"private readonly {ArgType.FullNameInCode()} {Usage};");
    }

    public void WriteAssignment(ISourceWriter writer)
    {
        writer.Write($"{Usage} = {CtorArg};");
    }

    public Variable ToBaseCtorVariable()
    {
        return new BaseConstructorArgument(this);
    }
}

public class BaseConstructorArgument : Variable
{
    private readonly InjectedField _field;

    public BaseConstructorArgument(InjectedField field) : base(field.ArgType, field.Usage)
    {
        _field = field;
    }

    public override string Usage
    {
        get => _field.CtorArg;
        protected set
        {
            // nothing
        }
    }
}