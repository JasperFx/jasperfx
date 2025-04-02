namespace JasperFx.Events.Projections;

internal static class ReflectionExtensions
{
    /// <summary>
    /// Is the named method overridden by a type outside of JasperFx.Events
    /// </summary>
    /// <param name="type"></param>
    /// <param name="methodName"></param>
    /// <returns></returns>
    public static bool IsOverridden(this Type type, string methodName)
    {
        return type.GetMethod(methodName).DeclaringType.Assembly != typeof(ReflectionExtensions).Assembly;
    }
}