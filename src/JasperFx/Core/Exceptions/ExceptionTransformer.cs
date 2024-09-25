namespace JasperFx.Core.Exceptions;

/// <summary>
/// Helper to transform exceptions into hopefully more user friendly exceptions. Static wrapper around
/// ExceptionTransforms
/// </summary>
public static class ExceptionTransformer
{
    public static readonly ExceptionTransforms Transforms = new ExceptionTransforms();
    
    public static void WrapAndThrow(Exception exception)
    {
        Transforms.TransformAndThrow(exception);
    }
}