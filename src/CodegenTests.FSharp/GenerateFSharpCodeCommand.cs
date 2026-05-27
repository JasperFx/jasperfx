using JasperFx.CommandLine;

namespace CodegenTests.FSharp;

public class FSharpCodegenInput
{
    [Description("Optional explicit path for the generated Generated.fs file")]
    public string? FileFlag { get; set; }
}

/// <summary>
///     The <c>codegen-fsharp</c> command (jasperfx#383). Builds the fixed sample
///     <see cref="JasperFx.CodeGeneration.GeneratedAssembly" />, renders it as F# via
///     <see cref="JasperFx.CodeGeneration.GeneratedAssembly.GenerateFSharpCode" />, and writes
///     <c>Generated.fs</c> into the checked-in fixture project. The compile-gate test invokes
///     <see cref="Execute" /> directly to regenerate the fixture before building it; the command
///     shape (a real <see cref="JasperFxCommand{T}" />) is kept so it can later be registered with
///     a CLI host once the F# emit layer graduates out of the test fixtures.
/// </summary>
[Description("Generate the canonical F# code-generation sample into the checked-in fixture (jasperfx#383)",
    Name = "codegen-fsharp")]
public class GenerateFSharpCodeCommand : JasperFxCommand<FSharpCodegenInput>
{
    public GenerateFSharpCodeCommand()
    {
        Usage("Generate the F# sample").Arguments();
    }

    public override bool Execute(FSharpCodegenInput input)
    {
        var path = input.FileFlag ?? FSharpCodegenSample.DefaultGeneratedFilePath();
        var code = FSharpCodegenSample.GenerateCode();

        File.WriteAllText(path, code);
        Console.WriteLine($"Wrote generated F# to {path}");

        return true;
    }
}
