namespace JasperFx.RuntimeCompiler;

/// <summary>
///     Thrown when the Roslyn compilation of generated code for <paramref name="subject" /> fails.
/// </summary>
/// <remarks>
///     jasperfx#877: this derives from <see cref="JasperFx.CodeGeneration.CodeGenerationException" />
///     rather than from <see cref="Exception" />. The two types shared a simple name, so
///     <c>catch (CodeGenerationException)</c> caught whichever one the file's <c>using</c>
///     directives resolved and a stack trace naming "CodeGenerationException" was ambiguous.
///     Deriving keeps both names working — this is the same class of failure one layer down — and
///     makes a single catch cover both, which is what a caller writing that catch meant anyway.
/// </remarks>
public class CodeGenerationException : JasperFx.CodeGeneration.CodeGenerationException
{
    public CodeGenerationException(object subject, Exception ex) : base(
        $"Error while trying to generate code for '{subject}'", ex)
    {
        Subject = subject;
    }

    /// <summary>
    ///     Whatever was being generated — a type, a code file, a generator — as the throw site had
    ///     it. Exposed so a caller can tell the two layers apart without parsing the message.
    /// </summary>
    public object Subject { get; }
}
