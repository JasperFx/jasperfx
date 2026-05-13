using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events.Projections;

[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level: GetMethod on the projection Type. The projection type is preserved at the registration boundary on the caller side.")]
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
        return type.GetMethod(methodName)!.DeclaringType!.Assembly != typeof(ReflectionExtensions).Assembly;
    }
}