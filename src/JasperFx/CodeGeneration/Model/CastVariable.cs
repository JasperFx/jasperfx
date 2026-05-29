using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Model;

public class CastVariable : Variable
{
    public CastVariable(Variable parent, Type specificType) : base(specificType,
        $"(({specificType.FullNameInCode()}){parent.Usage})")
    {
        Dependencies.Add(parent);
        Inner = parent;
    }

    // strictly for easier testing
    public Variable Inner { get; }

    /// <summary>
    ///     F# has no C-style cast. Render an upcast (<c>:&gt;</c>) when the cast target is a base of the
    ///     inner variable's type, otherwise a dynamic downcast (<c>:?&gt;</c>). See jasperfx#395.
    /// </summary>
    public override string FSharpUsage
    {
        get
        {
            var op = VariableType.IsAssignableFrom(Inner.VariableType) ? ":>" : ":?>";
            return $"({Inner.FSharpUsage} {op} {VariableType.FSharpName()})";
        }
    }
}