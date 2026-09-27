using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
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

    Target Test => _ => _.DependsOn(TestCore, TestCodegen, TestCodegenFSharp, TestCommandLine, TestEvents, TestEventStore, TestSourceGenerators, TestAspire, TestMicrosoftExtensionsAI, SmokeTestAot, SmokeTestBuildTargets);
    
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
    
    // The F# acceptance gate: regenerates the fixture's Generated.fs from the emitters and compiles it
    // with the in-box F# compiler. Compile alone only proves the *committed* Generated.fs builds, so
    // without this target the fixture can silently drift from the emitters that produce it.
    //
    // Pinned to a single framework on purpose: the gate writes Generated.fs and builds the fixture, so
    // running both target frameworks would have two test runs racing on the same file. (CI's
    // DISABLE_TEST_PARALLELIZATION only serializes *within* an assembly, not across frameworks.)
    Target TestCodegenFSharp => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.src.CodegenTests_FSharp)
                .SetFramework("net9.0")
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

    // Both source generator suites. These are in the solution (so `compile` builds them, and they
    // break the build if they stop compiling) but were in no test target, meaning 45 tests covering
    // the two Roslyn generators built and never ran. Single-framework by project: both test
    // projects pin TargetFramework=net9.0 to match the netstandard2.0 generators they host.
    Target TestSourceGenerators => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.src.JasperFx_SourceGenerator_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());

            DotNetTest(c => c
                .SetProjectFile(Solution.src.JasperFx_Events_SourceGenerator_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // Aspire integration surface. net10.0-only, matching JasperFx.Aspire itself (Aspire 13 is
    // net10-first). Was likewise in the solution but in no test target.
    Target TestAspire => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.src.JasperFx_Aspire_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // The Microsoft.Extensions.AI adapter for IEmbeddingProvider (#813). Its own project so
    // Microsoft.Extensions.AI.Abstractions never reaches EventTests.
    Target TestMicrosoftExtensionsAI => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.src.JasperFx_Events_MicrosoftExtensionsAI_Tests)
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

    AbsolutePath ShippedEventsTargets =>
        RootDirectory / "src" / "JasperFx.Events" / "build" / "JasperFx.Events.targets";

    /// <summary>
    /// jasperfx#908 — the shipped MSBuild targets, imported and exercised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>src/JasperFx.Events/build/JasperFx.Events.targets</c> is packed to
    /// <c>buildTransitive/</c>, so NuGet imports it into every project that reaches JasperFx.Events
    /// directly or transitively — which is every Marten, Polecat and Fisher consumer. But it ships as
    /// <c>&lt;None&gt;</c> rather than an <c>&lt;Import&gt;</c>, so OUR OWN BUILD NEVER LOADS IT: the
    /// solution build and all ten test projects say nothing about whether that file is even well-formed
    /// XML, let alone whether its targets behave.
    /// </para>
    /// <para>
    /// That is not hypothetical. While implementing jasperfx#902 a <c>--</c> inside an XML comment made
    /// MSBuild refuse the whole file (MSB4024); <c>dotnet build</c> on the solution passed clean with 0
    /// errors and the full suite would have too, while every consumer's build would have failed on an
    /// error naming our file. It was caught only by hand-importing the targets, which is what this
    /// automates. It is the least-tested and widest-reach code in the repository.
    /// </para>
    /// <para>
    /// ⚠️ <b>Each arrangement invokes the target BY NAME</b>, never through <c>-t:Build</c> on an
    /// SDK project with a stubbed <c>CoreCompile</c>. That was tried first and produced a FALSE
    /// NEGATIVE: the target's <c>BeforeTargets="CoreCompile"</c> hook does not wire up against a
    /// redefined <c>CoreCompile</c>, so the warning silently did not fire and the logic looked broken
    /// when it was correct. The <c>BeforeTargets</c> wiring is not what needs testing; the target body is.
    /// </para>
    /// </remarks>
    Target SmokeTestBuildTargets => _ => _
        .Executes(() =>
        {
            var scratch = TemporaryDirectory / "targets-smoke";
            scratch.CreateOrCleanDirectory();

            // Two fake analyzer paths. Nothing loads them -- the targets match on file name only.
            var copyA = scratch / "a" / "JasperFx.Events.SourceGenerator.dll";
            var copyB = scratch / "b" / "JasperFx.Events.SourceGenerator.dll";

            void Arrange(string name, string properties, string analyzers, string target,
                string mustContain, string mustNotContain)
            {
                var file = scratch / $"{name}.csproj";

                // Deliberately NOT Microsoft.NET.Sdk: no SDK targets to collide with, nothing to
                // restore, and the target under test is invoked directly. Keeps each arrangement to
                // about a second.
                file.WriteAllText($"""
                    <Project>
                      <PropertyGroup>
                        {properties}
                      </PropertyGroup>
                      <ItemGroup>
                        {analyzers}
                      </ItemGroup>
                      <Import Project="{ShippedEventsTargets}" />
                    </Project>
                    """);

                var outputLines = DotNet($"msbuild \"{file}\" -t:{target} -nologo",
                        workingDirectory: scratch, logOutput: false, exitHandler: _ => null)
                    .Select(x => x.Text)
                    .ToArray();
                var output = string.Join(Environment.NewLine, outputLines);

                // MSB4024 -- MSBuild cannot load the file at all -- is the failure this target exists
                // for, so it is named explicitly.
                if (output.Contains("MSB4024"))
                {
                    Assert.Fail(
                        $"{ShippedEventsTargets.Name} could not be loaded by MSBuild. This breaks EVERY consumer " +
                        $"of the stack and our own build cannot see it (jasperfx#908). A '--' inside an XML " +
                        $"comment is the usual cause.\n{output}");
                }

                // Any OTHER MSBuild error fails too, and that is a lesson rather than belt-and-braces:
                // while writing this, the harness set the reserved MSBuildProjectName property, and the
                // resulting MSB4004 was reported as "JFXEVT902 absent" -- a confusing symptom two steps
                // from its cause. An unexpected error is never evidence about the diagnostic under test.
                if (output.Contains("error MSB"))
                {
                    Assert.Fail($"[{name}] MSBuild reported an unexpected error, so this arrangement " +
                                $"proves nothing about the target under test.\n{output}");
                }

                if (mustContain.IsNullOrEmpty() == false && !output.Contains(mustContain))
                {
                    Assert.Fail($"[{name}] expected '{mustContain}' in the output but it was absent.\n{output}");
                }

                if (mustNotContain.IsNullOrEmpty() == false && output.Contains(mustNotContain))
                {
                    Assert.Fail($"[{name}] expected NO '{mustNotContain}' in the output but it was present.\n{output}");
                }
            }

            var both = $"""
                <Analyzer Include="{copyA}" />
                        <Analyzer Include="{copyB}" />
                """;
            var one = $"""<Analyzer Include="{copyA}" />""";

            const string dedupe = "JasperFxEventsDedupeSourceGeneratorAnalyzers";
            const string dedupeTarget = "JasperFxEventsDedupeSourceGeneratorAnalyzers";
            const string assertTarget = "JasperFxEventsAssertSourceGeneratorIsAttached";

            // jasperfx#902: the duplication is about to break the build and the dedupe is off, so this
            // is the one case the target can see AND must report.
            Arrange("dupes_dedupe_off", $"<{dedupe}>false</{dedupe}>", both, dedupeTarget,
                mustContain: "warning JFXEVT902:", mustNotContain: null);

            // Handled: one copy kept, and deliberately NOT a warning -- a packaging accident the
            // consumer did nothing to cause, now harmless.
            Arrange("dupes_dedupe_on", "", both, dedupeTarget,
                mustContain: null, mustNotContain: "warning JFXEVT902:");

            // The ordinary build, which must stay completely silent.
            Arrange("single_copy", $"<{dedupe}>false</{dedupe}>", one, dedupeTarget,
                mustContain: null, mustNotContain: "warning JFXEVT902:");

            Arrange("no_analyzers", $"<{dedupe}>false</{dedupe}>", "", dedupeTarget,
                mustContain: null, mustNotContain: "warning JFXEVT902:");

            // jasperfx#892: the opt-in assertion. Kept here because it DependsOnTargets the dedupe
            // target above, so a change to that one can break this without touching its own code.
            Arrange("required_but_absent",
                "<JasperFxEventsRequireSourceGenerator>true</JasperFxEventsRequireSourceGenerator>", "",
                assertTarget, mustContain: "error JFXEVT900:", mustNotContain: null);

            Arrange("required_and_present",
                "<JasperFxEventsRequireSourceGenerator>true</JasperFxEventsRequireSourceGenerator>", one,
                assertTarget, mustContain: null, mustNotContain: "error JFXEVT900:");

            Serilog.Log.Information(
                "Shipped MSBuild targets verified in 6 arrangements: {File}", ShippedEventsTargets);
        });

    Target NugetPack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var projects = new[]
            {
                Solution.JasperFx,
                Solution.JasperFx_RuntimeCompiler,
                Solution.JasperFx_Events,
                Solution.JasperFx_Events_ComplianceTests,
                Solution.src.JasperFx_Events_SourceGenerator,
                Solution.src.JasperFx_SourceGenerator,
                Solution.src.JasperFx_Aspire,
                Solution.src.JasperFx_Events_MicrosoftExtensionsAI
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

    [Parameter(
        "Push even when the line version is already on nuget.org. Only for retrying a publish that failed partway.")]
    readonly bool AllowRepublish;

    /// <summary>
    /// Fail before pushing if $(JasperFxVersion) is already published.
    /// </summary>
    /// <remarks>
    /// NugetPush uses --skip-duplicate, which is correct -- JasperFx.RuntimeCompiler is deliberately
    /// pinned off the line and skips every time, and a publish that failed partway has to be safe to
    /// retry. The cost is that running the workflow WITHOUT bumping the version skips all seven
    /// packages and still exits 0, so a shippable merge can "release" nothing and report success. The
    /// only signal is a consumer restore resolving the old version, a long way from the cause.
    ///
    /// Checks the JasperFx package alone: it carries the line version, whereas RuntimeCompiler
    /// intentionally does not. A network failure warns rather than fails -- this is a safety net, and
    /// an unreachable nuget.org should not block a release.
    /// </remarks>
    Target AssertVersionIsUnpublished => _ => _
        .OnlyWhenDynamic(() => !AllowRepublish)
        .Executes(() =>
        {
            var version = XDocument.Load(RootDirectory / "Directory.Build.props")
                .Descendants("JasperFxVersion")
                .Select(x => x.Value.Trim())
                .FirstOrDefault();

            if (string.IsNullOrEmpty(version))
            {
                throw new Exception("Could not read JasperFxVersion from Directory.Build.props");
            }

            string[] published;
            try
            {
                using var client = new HttpClient();
                var response = client
                    .GetAsync("https://api.nuget.org/v3-flatcontainer/jasperfx/index.json")
                    .GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                response.EnsureSuccessStatusCode();

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                published = JsonDocument.Parse(json).RootElement.GetProperty("versions")
                    .EnumerateArray().Select(x => x.GetString()).ToArray();
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e,
                    "Could not reach nuget.org to check whether {Version} is already published; pushing anyway",
                    version);
                return;
            }

            if (published.Contains(version, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception(
                    $"JasperFx {version} is already published to nuget.org. Every package would be skipped as a "
                    + "duplicate and this run would still report success. Bump JasperFxVersion in "
                    + "Directory.Build.props, or pass --allow-republish to retry a publish that failed partway.");
            }

            Serilog.Log.Information("JasperFx {Version} is not yet published; proceeding", version);
        });

    Target NugetPush => _ => _
        .DependsOn(NugetPack)
        .DependsOn(AssertVersionIsUnpublished)
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
