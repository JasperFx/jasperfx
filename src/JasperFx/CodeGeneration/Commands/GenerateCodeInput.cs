using JasperFx.CommandLine;

namespace JasperFx.CodeGeneration.Commands;

public class GenerateCodeInput : NetCoreInput
{
    [Description("Action to take ")] public CodeAction Action { get; set; } = CodeAction.preview;

    [Description("Optionally limit the preview to only one type of code generation")]
    public string? TypeFlag { get; set; }

    [Description("Target language for the generated code (csharp or fsharp). Default is csharp")]
    public CodegenLanguage LanguageFlag { get; set; } = CodegenLanguage.csharp;

    [Description("Start the IHost instead of just building it. Use this when code generation participants are registered during host startup (e.g., ASP.NET Core Startup.Configure)")]
    public bool StartFlag { get; set; }
}