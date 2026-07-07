using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Test, x => x.SmokeTestCommands);
    
    [Solution(GenerateProjects = true)] readonly Solution Solution;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _.DependsOn(TestCore, TestCodegen, TestCommandLine, TestEvents, TestEventStore, SmokeTestAot);
    
    Target TestCore => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.CoreTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
    
    Target TestCodegen => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.CodegenTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
    
    Target TestCommandLine => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.CommandLineTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
    
    Target TestEvents => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.EventTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target TestEventStore => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.EventStoreTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target SmokeTestCommands => _ => _.DependsOn(Compile)
        .Executes(() =>
        {
            DotNet("run --framework net9.0 -- check-env", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet("run --framework net9.0 -- describe", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet("run --framework net9.0 -- describe --file description.txt", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet("run --framework net9.0 -- describe --environment Testing --applicationName Different --contentRoot /bin", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet("run --framework net9.0 -- describe --environment=Testing --applicationName=Different --contentRoot=/bin", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet("run --framework net9.0 -- codegen preview --start", Solution.TestHarnesses.GeneratorTarget.Directory);

            // Validate the `--language fsharp` codegen flag is wired through the CLI and emits F#.
            // (Compilable/runnable pre-generated F# is proven downstream against real handler chains.)
            DotNet("run --framework net9.0 -- codegen preview --language fsharp --start", Solution.TestHarnesses.GeneratorTarget.Directory);
        });

    /// <summary>
    ///     AOT-clean consumer smoke test (jasperfx#213). The JasperFx.AotSmoke
    ///     project sets IsAotCompatible=true + promotes IL2026 / IL3050 / IL2046
    ///     / IL2070 / IL2075 (the full AOT analyzer set) to errors and exercises
    ///     a representative slice of the AOT-clean JasperFx + JasperFx.Events
    ///     surface. The build fails if a previously-AOT-clean API gains an
    ///     annotation, or if Program.cs is changed to call into a reflective
    ///     surface. Also runs the program to confirm runtime behavior is intact.
    /// </summary>
    Target SmokeTestAot => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution.TestHarnesses.JasperFx_AotSmoke)
                .SetConfiguration(Configuration)
                .EnableNoRestore());

            DotNet("run --framework net10.0 --no-build", Solution.TestHarnesses.JasperFx_AotSmoke.Directory);
        });

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    
    Target NugetPack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var projects = new[]
            {
                Solution.JasperFx,
                Solution.JasperFx_RuntimeCompiler,
                Solution.JasperFx_Events,
                Solution.src.JasperFx_Events_SourceGenerator,
                Solution.src.JasperFx_SourceGenerator,
                Solution.src.JasperFx_Aspire
            };

            foreach (var project in projects)
            {
                DotNetPack(s => s
                    .SetProject(project)
                    .SetOutputDirectory(ArtifactsDirectory)
                    .SetConfiguration(Configuration.Release));
            }
        });
    
    
    [Parameter("Nuget Api Key")] [Secret] readonly string NugetApiKey;

    Target NugetPush => _ => _
        .DependsOn(NugetPack)
        .Requires(() => !string.IsNullOrEmpty(NugetApiKey))
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetTargetPath(ArtifactsDirectory / "*.nupkg")
                .EnableSkipDuplicate()
                .EnableNoSymbols()
                .SetApiKey(NugetApiKey));
        });
}
