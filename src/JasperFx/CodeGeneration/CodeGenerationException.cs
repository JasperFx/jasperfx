using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration;

public class CodeGenerationException : Exception
{
    public CodeGenerationException(ICodeFile file, Exception? innerException) : base(
        $"Error trying to generate the code for file {file.FileName} of type {file.GetType().NameInCode()}",
        innerException)
    {
    }
}