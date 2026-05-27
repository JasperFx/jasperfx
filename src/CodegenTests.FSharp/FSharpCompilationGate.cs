using System.Diagnostics;
using Shouldly;
using Xunit.Abstractions;

namespace CodegenTests.FSharp;

/// <summary>
///     The milestone-1 acceptance gate for F# code generation (jasperfx#383): runs the
///     <c>codegen-fsharp</c> command to (re)generate the fixture's <c>Generated.fs</c>, then shells
///     out to <c>dotnet build</c> on the checked-in F# fixture and asserts a clean (exit 0) build.
///     This proves the emitted F# actually compiles with the in-box F# compiler — no extra CI tooling.
/// </summary>
public class FSharpCompilationGate
{
    private readonly ITestOutputHelper _output;

    public FSharpCompilationGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void generated_fsharp_compiles_via_dotnet_build()
    {
        // 1. Run the command exactly as a developer would, writing Generated.fs into the fixture.
        var command = new GenerateFSharpCodeCommand();
        command.Execute(new FSharpCodegenInput()).ShouldBeTrue();

        var generatedFile = FSharpCodegenSample.DefaultGeneratedFilePath();
        File.Exists(generatedFile).ShouldBeTrue();
        _output.WriteLine(File.ReadAllText(generatedFile));

        // 2. Compile the fixture with the F# compiler that ships in the SDK.
        var fixtureProject = FSharpCodegenSample.FixtureProjectPath();
        var (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");

        _output.WriteLine(output);
        exitCode.ShouldBe(0);
    }

    private static (int ExitCode, string Output) RunDotnet(string arguments)
    {
        var info = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // A nested `dotnet build` reusing MSBuild server nodes can hang the child; disable it so
        // the build runs (and exits) in-process and we always get a deterministic exit code.
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info)!;

        // Read both streams concurrently to avoid a deadlock if either child buffer fills.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
