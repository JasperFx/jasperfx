namespace JasperFx.CommandLine.Parsing;

/// <summary>
/// Interface for source-generated input model parsers that produce
/// optimized ITokenHandler lists without runtime reflection.
/// </summary>
public interface IGeneratedInputParser
{
    Type InputType { get; }
    List<ITokenHandler> BuildHandlers();
}
