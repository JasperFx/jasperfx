using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

/// <summary>
///     Thrown when code generation for a single <see cref="ICodeFile" /> fails — the exception the
///     <c>codegen preview</c> / <c>codegen write</c> commands surface, and the one
///     <see cref="GeneratorCompilationFailureException" /> carries as an inner exception.
/// </summary>
/// <remarks>
///     jasperfx#877: <c>JasperFx.RuntimeCompiler.CodeGenerationException</c> — the same class of
///     failure one layer down, raised while compiling rather than while assembling types — derives
///     from this one, so a single <c>catch (CodeGenerationException)</c> catches both and it no
///     longer matters which of the two a <c>using</c> happens to resolve.
/// </remarks>
public class CodeGenerationException : Exception
{
    public CodeGenerationException(ICodeFile file, Exception? innerException) : base(
        $"Error trying to generate the code for file {file.FileName} of type {file.GetType().NameInCode()}",
        innerException)
    {
    }

    /// <summary>
    ///     For subclasses that describe a narrower failure and therefore need their own message.
    /// </summary>
    protected CodeGenerationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
