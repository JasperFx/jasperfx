namespace JasperFx.CodeGeneration;

/// <summary>
/// Target language for generated code. C# is the default and supports both the dynamic (runtime
/// Roslyn) and pre-generated (static) models. F# targets the pre-generated/static model only.
/// </summary>
public enum CodegenLanguage
{
    csharp,
    fsharp
}
