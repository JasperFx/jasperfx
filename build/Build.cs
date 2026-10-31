using System;
using System.IO;
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

    // The one framework every target that must pin to a single TFM runs on (the F# gate, the CLI smoke
    // commands). Was a literal net9.0 in several places, which breaks the moment net9.0 is dropped from
    // TargetFrameworks. net10.0 is in the set before and after that, so it is safe on both sides.
    [Parameter("Target framework for single-framework targets (F# gate, command smoke tests) - Default is 'net10.0'")]
    readonly string SingleFramework = "net10.0";

    [Parameter("Run the test targets on one target framework, e.g. net10.0. Default: every framework the test project declares")]
    readonly string Framework;

    [Parameter("Stamp this version on the packages, e.g. 3.0.0-local.1. Default: JasperFxVersion from Directory.Build.props", Name = "Version")]
    readonly string PackVersion;

    [Parameter("Copy the packed packages into this folder, e.g. ~/code/local-nuget")]
    readonly AbsolutePath Feed;

    // Which Stoat/Bobcat gate this run belongs to. Test targets inherit it through the environment; it is
    // logged so a run's correlation is visible in the build output rather than silent.
    static void ReportRunTag(string target)
    {
        var tag = Environment.GetEnvironmentVariable("BOBCAT_RUN_TAG");
        Serilog.Log.Information(string.IsNullOrEmpty(tag)
            ? "{Target}: BOBCAT_RUN_TAG is not set; this run is not correlated to a Stoat gate"
            : "{Target}: running under BOBCAT_RUN_TAG={Tag}", target, tag);
    }

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

    Target Test => _ => _.DependsOn(TestCore, TestCodegen, TestCodegenFSharp, TestCommandLine, TestEvents, TestEventStore, TestInMemoryEventStore, TestStress, TestSourceGenerators, TestAspire, TestMicrosoftExtensionsAI, SmokeTestAot, SmokeTestBuildTargets);
    
    Target TestCore => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestCore");
            DotNetTest(c => c
                .SetProjectFile(Solution.CoreTests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
    
    Target TestCodegen => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestCodegen");
            DotNetTest(c => c
                .SetProjectFile(Solution.CodegenTests)
                .SetFramework(Framework)
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
            ReportRunTag("TestCodegenFSharp");
            DotNetTest(c => c
                .SetProjectFile(Solution.src.CodegenTests_FSharp)
                .SetFramework(SingleFramework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target TestCommandLine => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestCommandLine");
            DotNetTest(c => c
                .SetProjectFile(Solution.CommandLineTests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
    
    Target TestEvents => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestEvents");
            DotNetTest(c => c
                .SetProjectFile(Solution.EventTests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target TestEventStore => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestEventStore");
            DotNetTest(c => c
                .SetProjectFile(Solution.EventStoreTests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // jasperfx#964: the event compliance suites, compiled as source and run against the in-memory
    // prototyping store.
    Target TestInMemoryEventStore => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestInMemoryEventStore");
            DotNetTest(c => c
                .SetProjectFile(Solution.InMemoryEventStoreTests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // Tests that deliberately drive many threads, or many rounds of a race, to provoke a concurrency bug.
    // They have their own project, which runs its tests one at a time, so they never compete for the thread
    // pool with the timing-sensitive tests in the other suites (#1148: the coordinator overlap tests running
    // beside HighWaterAgent's 200ms staleness watchdog in EventTests).
    Target TestStress => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestStress");
            DotNetTest(c => c
                .SetProjectFile(Solution.StressTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // Both source generator suites. These are in the solution (so `compile` builds them, and they
    // break the build if they stop compiling) but were in no test target, meaning 45 tests covering
    // the two Roslyn generators built and never ran. Single-framework by project: both test
    // projects pin one TargetFramework to host the netstandard2.0 generators they exercise.
    Target TestSourceGenerators => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("TestSourceGenerators");
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
            ReportRunTag("TestAspire");
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
            ReportRunTag("TestMicrosoftExtensionsAI");
            DotNetTest(c => c
                .SetProjectFile(Solution.src.JasperFx_Events_MicrosoftExtensionsAI_Tests)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    Target SmokeTestCommands => _ => _.DependsOn(Compile)
        .Executes(() =>
        {
            ReportRunTag("SmokeTestCommands");
            DotNet($"run --framework {SingleFramework} -- check-env", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet($"run --framework {SingleFramework} -- describe", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet($"run --framework {SingleFramework} -- describe --file description.txt", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet($"run --framework {SingleFramework} -- describe --environment Testing --applicationName Different --contentRoot /bin", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet($"run --framework {SingleFramework} -- describe --environment=Testing --applicationName=Different --contentRoot=/bin", Solution.TestHarnesses.CommandLineRunner.Directory);
            DotNet($"run --framework {SingleFramework} -- codegen preview --start", Solution.TestHarnesses.GeneratorTarget.Directory);

            // Validate the `--language fsharp` codegen flag is wired through the CLI and emits F#.
            // (Compilable/runnable pre-generated F# is proven downstream against real handler chains.)
            DotNet($"run --framework {SingleFramework} -- codegen preview --language fsharp --start", Solution.TestHarnesses.GeneratorTarget.Directory);
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

    /// <summary>
    /// jasperfx#777: the AOT roots companion proven under a real ILC publish, which nothing else in the repo
    /// runs (<see cref="SmokeTestAot" /> is the analyzer only). Publishes src/JasperFx.AotRootsSmoke twice
    /// with PublishAot: rooted, the native binary must resolve its pre-generated type through
    /// TypeLoadMode.Static (exit 0); unrooted, with the companion compiled out, it must NOT (exit 2). The
    /// negative control is what stops the gate passing because ILC happened to keep the type anyway.
    /// Deliberately not part of <see cref="Test" />: an ILC publish is slow, and the surface rarely changes,
    /// so it runs from its own scheduled / manual workflow (aot-roots.yml).
    /// </summary>
    Target SmokeTestAotRoots => _ => _
        .Executes(() =>
        {
            var project = RootDirectory / "src" / "JasperFx.AotRootsSmoke" / "JasperFx.AotRootsSmoke.csproj";
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var output = ArtifactsDirectory / "aot-roots";
            var binary = OperatingSystem.IsWindows() ? "JasperFx.AotRootsSmoke.exe" : "JasperFx.AotRootsSmoke";

            int publishAndRun(string variant, string extra)
            {
                DotNet($"publish \"{project}\" -c Release -r {rid} -o \"{output / variant}\" {extra}");

                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((output / variant / binary).ToString())
                {
                    UseShellExecute = false
                })!;
                process.WaitForExit();
                return process.ExitCode;
            }

            var rooted = publishAndRun("rooted", "");
            Assert.True(rooted == 0,
                $"The rooted native binary exited {rooted}: the AOT roots companion did not keep the pre-generated type resolvable through TypeLoadMode.Static under Native AOT.");

            var unrooted = publishAndRun("unrooted", "-p:AotRootsDisabled=true");
            Assert.True(unrooted == 2,
                $"The unrooted native binary exited {unrooted}, expected 2 (type not found). Without the companion ILC should have trimmed the type; if it did not, the rooted pass proves nothing.");
        });

    /// <summary>
    /// jasperfx#1141: the JasperFx command line, run from a Native AOT binary. Publishes
    /// src/JasperFx.AotCliSmoke (a WebApplication wired like the app in the issue) with PublishAot, then
    /// runs the native binary through help, help &lt;command&gt;, an application command, check-env,
    /// describe and run. Before #1141 the native binary failed every one of them, `run` included, so
    /// an AOT app that ended in RunJasperFxCommands could not start at all, while the same app worked
    /// under `dotnet run`. Runs on every PR from its own job in dotnet.yml.
    /// </summary>
    Target SmokeTestAotCli => _ => _
        .Executes(() =>
        {
            var project = RootDirectory / "src" / "JasperFx.AotCliSmoke" / "JasperFx.AotCliSmoke.csproj";
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            var output = ArtifactsDirectory / "aot-cli";
            var binary = output / (OperatingSystem.IsWindows() ? "JasperFx.AotCliSmoke.exe" : "JasperFx.AotCliSmoke");

            DotNet($"publish \"{project}\" -c Release -r {rid} -o \"{output}\"");

            (int exitCode, string text) run(params string[] args)
            {
                var start = new System.Diagnostics.ProcessStartInfo(binary.ToString())
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = output
                };
                foreach (var arg in args) start.ArgumentList.Add(arg);

                // Lets `run` prove itself and exit (StopAfterStart in the smoke app), on a free port.
                start.Environment["AOT_CLI_SMOKE_STOP_AFTER_START"] = "true";
                start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

                using var process = System.Diagnostics.Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail($"The native binary did not exit within a minute for '{string.Join(" ", args)}'.");
                }

                // Spectre.Console detects CI (GITHUB_ACTIONS) and writes ANSI styling even to redirected
                // output, which splits text like "run - Start" around escape sequences. Assert on plain text.
                var text = System.Text.RegularExpressions.Regex.Replace(stdout.Result + stderr.Result,
                    @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
                Serilog.Log.Information("{Binary} {Args} exited {ExitCode}:\n{Output}", "JasperFx.AotCliSmoke",
                    string.Join(" ", args), process.ExitCode, text);
                return (process.ExitCode, text);
            }

            void noException(string text, string args)
            {
                Assert.False(text.Contains("Exception", StringComparison.Ordinal),
                    $"The native binary threw for '{args}' (see the output above).");
            }

            // Every command from all three assemblies: JasperFx's own, JasperFx.Events' and the app's.
            var help = run("help");
            noException(help.text, "help");
            Assert.True(help.exitCode == 0, $"'help' exited {help.exitCode}.");
            foreach (var command in new[]
                     {
                         "check-env", "codegen", "describe", "help", "resources", "run",
                         "event-query", "projection-run", "projections", "stream-query",
                         "greet"
                     })
            {
                Assert.True(help.text.Contains($"  {command} ", StringComparison.Ordinal),
                    $"'help' did not list the '{command}' command.");
            }

            // `help <command>` renders usage and exits non-zero by design (see smoke-test-help in
            // dotnet.yml), so only the output is checked.
            foreach (var (command, expected) in new[]
                     {
                         ("run", "run - Start and run this .Net application"),
                         ("projections", "projections - Asynchronous projection"),
                         ("greet", "[-t, --tags <tags1 tags2 tags3 ...>]")
                     })
            {
                var usage = run("help", command);
                noException(usage.text, $"help {command}");
                Assert.True(usage.text.Contains(expected, StringComparison.Ordinal),
                    $"'help {command}' did not render its usage.");
            }

            // An application command: generated factory, generated parser (with an enumerable flag the
            // reflective parser would close List<T> for) and a generated [InjectService] injector.
            var greet = run("greet", "World", "--shout", "--tags", "a", "b");
            noException(greet.text, "greet");
            Assert.True(greet.exitCode == 0, $"'greet' exited {greet.exitCode}.");
            Assert.True(greet.text.Contains("HELLO, WORLD", StringComparison.Ordinal)
                        && greet.text.Contains("tags: a,b", StringComparison.Ordinal),
                "'greet' did not parse its input or inject its service.");

            var checkEnv = run("check-env");
            noException(checkEnv.text, "check-env");
            Assert.True(checkEnv.exitCode == 0, $"'check-env' exited {checkEnv.exitCode}.");

            var describe = run("describe");
            noException(describe.text, "describe");
            Assert.True(describe.exitCode == 0, $"'describe' exited {describe.exitCode}.");
            Assert.True(describe.text.Contains("Referenced Assemblies", StringComparison.Ordinal),
                "'describe' did not write its referenced assemblies.");

            // `run`, both by name and as the default with no arguments: the case in the issue.
            foreach (var args in new[] { new[] { "run" }, Array.Empty<string>() })
            {
                var name = args.Length == 0 ? "(no arguments)" : "run";
                var started = run(args);
                noException(started.text, name);
                Assert.True(started.exitCode == 0, $"'{name}' exited {started.exitCode}.");
                Assert.True(started.text.Contains("AOT CLI smoke: host started", StringComparison.Ordinal),
                    $"'{name}' did not start the host.");
            }
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

            // jasperfx#1138: the SDK's conflict resolution dropped a pinned copy (it lands in
            // _ConflictPackageFiles) and the higher-versioned bundled copy was then removed by a project
            // target, so nothing reached the compiler. The refused answer: a generator whose version
            // differs from the runtime's never produces nothing without saying so.
            const string lostTarget = "JasperFxEventsReportLostSourceGenerator";
            var dropped = $"""<_ConflictPackageFiles Include="{copyA}" />""";

            Arrange("a_generator_at_another_version_never_produces_nothing_without_saying_so", "", dropped,
                lostTarget, mustContain: "warning JFXEVT1138:", mustNotContain: null);

            // A version difference on its own is harmless: the higher copy reaches the compiler and runs.
            Arrange("dropped_lower_copy_with_higher_copy_compiled", "", $"""
                {dropped}
                        <Analyzer Include="{copyB}" />
                """, lostTarget, mustContain: null, mustNotContain: "warning JFXEVT1138:");

            // A project with no generator anywhere, the ordinary non-event-sourced project, stays silent.
            Arrange("no_generator_anywhere", "", "", lostTarget,
                mustContain: null, mustNotContain: "warning JFXEVT1138:");

            Arrange("lost_generator_report_turned_off",
                "<JasperFxEventsReportLostSourceGenerator>false</JasperFxEventsReportLostSourceGenerator>", dropped,
                lostTarget, mustContain: null, mustNotContain: "warning JFXEVT1138:");

            Serilog.Log.Information(
                "Shipped MSBuild targets verified in 10 arrangements: {File}", ShippedEventsTargets);
        });

    /// <summary>
    /// The train's local pack: <c>./build.sh Pack --version 3.0.0-local.1 --feed ~/code/local-nuget</c>.
    /// </summary>
    /// <remarks>
    /// --version goes through JasperFxVersion, never Version: the line packages read JasperFxVersion, and
    /// JasperFx.RuntimeCompiler keeps its own VersionPrefix off the line, which a -p:Version would silently
    /// restamp. It takes the prerelease suffix instead, so 3.0.0-local.1 also yields 6.0.0-local.1 rather
    /// than a RuntimeCompiler whose number is already on nuget.org depending on an older JasperFx. Use a
    /// fresh -local.N for every pack, because a repeated version is served from ~/.nuget/packages rather
    /// than the feed.
    /// </remarks>
    Target Pack => _ => _
        .DependsOn(NugetPack);

    Target NugetPack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var started = DateTime.UtcNow;

            var projects = new[]
            {
                Solution.JasperFx,
                Solution.JasperFx_RuntimeCompiler,
                Solution.JasperFx_Events,
                Solution.JasperFx_Events_ComplianceTests,
                Solution.src.JasperFx_Events_SourceGenerator,
                Solution.src.JasperFx_SourceGenerator,
                Solution.src.JasperFx_Aspire,
                Solution.src.JasperFx_Events_MicrosoftExtensionsAI,
                Solution.src.JasperFx_Events_InMemory
            };

            foreach (var project in projects)
            {
                DotNetPack(s => s
                    .SetProject(project)
                    .SetOutputDirectory(ArtifactsDirectory)
                    .SetConfiguration(Configuration.Release)
                    .When(_ => !string.IsNullOrEmpty(PackVersion), x => x
                        .SetProperty("JasperFxVersion", PackVersion)
                        .SetVersionSuffix(PackVersion.Contains('-') ? PackVersion[(PackVersion.IndexOf('-') + 1)..] : null)));
            }

            if (Feed == null) return;

            // Only what this run packed: artifacts/ is never cleaned, so it still holds earlier versions.
            // The feed is a flat folder, which is the layout Stoat reads and nuget.config points at.
            Feed.CreateDirectory();
            var packed = ArtifactsDirectory.GlobFiles("*.nupkg").Where(x => File.GetLastWriteTimeUtc(x) >= started).ToArray();
            foreach (var package in packed)
            {
                package.CopyToDirectory(Feed, ExistsPolicy.FileOverwrite);
            }

            Serilog.Log.Information("Copied {Count} packages to {Feed}", packed.Length, Feed);
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
