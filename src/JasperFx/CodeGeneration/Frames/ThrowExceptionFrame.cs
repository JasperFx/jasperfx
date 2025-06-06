using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Frames;

public class ThrowExceptionFrame<T> : CodeFrame where T : Exception
{
    public ThrowExceptionFrame(params object[] values) : base(false, ToFormat(values), values)
    {
    }

    public static string ToFormat(object[] values)
    {
        var index = 0;
        var parameters = values.Select(x => "{" + index++ + "}").Join(", ");

        return $"throw new {typeof(T).FullNameInCode()}({parameters});";
    }
}
