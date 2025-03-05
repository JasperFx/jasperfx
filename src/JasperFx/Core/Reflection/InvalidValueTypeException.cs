namespace JasperFx.Core.Reflection;

public class InvalidValueTypeException: Exception
{
    public InvalidValueTypeException(Type type, string message) : base($"Type {type.FullNameInCode()} cannot be used as a value type by Marten. " + message)
    {
    }
}
