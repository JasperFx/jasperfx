using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests;

public class DynamicCodeBuilderTests
{
    // #227: the three CLI codegen entry points (preview / write / test) used to
    // bypass the AssertServiceLocationsAreAllowed hook that downstream consumers
    // (Wolverine, Marten) rely on to enforce ServiceLocationPolicy.NotAllowed at
    // generation time. The runtime compile path (DynamicTypeLoader.Initialize)
    // already called the hook; only the CLI paths were silently dropping it.
    // These tests assert each entry point now invokes the hook once per ICodeFile
    // that produced at least one ServiceLocationReport.

    [Fact]
    public void generate_all_code_invokes_assertion_per_file()
    {
        var recorder = new RecordingFile("Alpha");
        var source = new FakeServiceVariableSource(reportName: "needs-lookup");
        var builder = BuildWithCollection(recorder, source);

        builder.GenerateAllCode();

        recorder.AssertionCalls.ShouldBe(1);
        recorder.LastReports.ShouldNotBeNull();
        recorder.LastReports!.Length.ShouldBe(1);
    }

    [Fact]
    public void generate_code_for_namespace_invokes_assertion_per_file()
    {
        var recorder = new RecordingFile("Beta");
        var source = new FakeServiceVariableSource(reportName: "needs-lookup");
        var builder = BuildWithCollection(recorder, source);

        builder.GenerateCodeFor("RecordedNamespace");

        recorder.AssertionCalls.ShouldBe(1);
    }

    [Fact]
    public void try_build_and_compile_all_invokes_assertion_per_file()
    {
        var recorder = new RecordingFile("Gamma");
        var source = new FakeServiceVariableSource(reportName: "needs-lookup");
        var builder = BuildWithCollection(recorder, source);

        builder.TryBuildAndCompileAll((_, _) => { });

        recorder.AssertionCalls.ShouldBe(1);
    }

    [Fact]
    public void no_assertion_when_no_service_locations_reported()
    {
        var recorder = new RecordingFile("Delta");
        var source = new FakeServiceVariableSource(reportName: null);
        var builder = BuildWithCollection(recorder, source);

        builder.GenerateAllCode();
        builder.TryBuildAndCompileAll((_, _) => { });

        recorder.AssertionCalls.ShouldBe(0);
    }

    [Fact]
    public void no_assertion_when_collection_does_not_use_services()
    {
        var recorder = new RecordingFile("Epsilon");
        var source = new FakeServiceVariableSource(reportName: "needs-lookup");
        var builder = new DynamicCodeBuilder(
            new ServiceCollection().BuildServiceProvider(),
            new ICodeFileCollection[] { new ServicelessCollection(recorder) })
        {
            ServiceVariableSource = source
        };

        builder.GenerateAllCode();

        recorder.AssertionCalls.ShouldBe(0);
    }

    // #2991: across files sharing one IServiceVariableSource (the CLI codegen path), a per-file
    // ServiceProviderSource override (TryReplaceServiceProvider -> true, e.g. HTTP's
    // httpContext.RequestServices) must be applied for that file and RESET before the next file so it
    // cannot leak into a file that should keep the default isolated-and-scoped provider.
    [Fact]
    public void per_file_service_provider_override_is_isolated_to_its_own_file()
    {
        var httpStyle = new RecordingFile("HttpStyle") { ReplacesProvider = true };
        var plain = new RecordingFile("Plain");
        var source = new FakeServiceVariableSource(reportName: null);

        var builder = new DynamicCodeBuilder(
            new ServiceCollection().BuildServiceProvider(),
            new ICodeFileCollection[] { new RecordingCollection(httpStyle, plain) })
        {
            ServiceVariableSource = source
        };

        builder.GenerateAllCode();

        // Reset runs once per file; ReplaceServiceProvider runs only for the file that opts in — and the
        // reset between files prevents the override from leaking into the plain file.
        source.Calls.ShouldBe(new[] { "reset", "replace:httpContext.RequestServices", "reset" });
    }

    private static DynamicCodeBuilder BuildWithCollection(RecordingFile file, IServiceVariableSource source)
    {
        return new DynamicCodeBuilder(
            new ServiceCollection().BuildServiceProvider(),
            new ICodeFileCollection[] { new RecordingCollection(file) })
        {
            ServiceVariableSource = source
        };
    }

    private sealed class RecordingFile : ICodeFile
    {
        public RecordingFile(string fileName) { FileName = fileName; }

        public string FileName { get; }
        public int AssertionCalls { get; private set; }
        public ServiceLocationReport[]? LastReports { get; private set; }

        // When true, mimics an HTTP endpoint configured with ServiceProviderSource.FromHttpContextRequestServices.
        public bool ReplacesProvider { get; init; }

        public bool TryReplaceServiceProvider(out JasperFx.CodeGeneration.Model.Variable serviceProvider)
        {
            serviceProvider = default!;
            if (!ReplacesProvider) return false;

            serviceProvider = new JasperFx.CodeGeneration.Model.Variable(typeof(IServiceProvider), "httpContext.RequestServices");
            return true;
        }

        public void AssembleTypes(GeneratedAssembly assembly)
        {
            assembly.AddType(FileName + "Type", typeof(object));
        }

        public Task<bool> AttachTypes(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => Task.FromResult(false);

        public bool AttachTypesSynchronously(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => false;

        public void AssertServiceLocationsAreAllowed(ServiceLocationReport[] reports, IServiceProvider? services)
        {
            AssertionCalls++;
            LastReports = reports;
        }
    }

    private sealed class RecordingCollection : ICodeFileCollectionWithServices
    {
        private readonly ICodeFile[] _files;
        public RecordingCollection(params ICodeFile[] files) { _files = files; }
        public string ChildNamespace => "RecordedNamespace";
        public GenerationRules Rules { get; } = new("RecordedNamespace");
        public IReadOnlyList<ICodeFile> BuildFiles() => _files;
    }

    private sealed class ServicelessCollection : ICodeFileCollection
    {
        private readonly ICodeFile _file;
        public ServicelessCollection(ICodeFile file) { _file = file; }
        public string ChildNamespace => "Serviceless";
        public GenerationRules Rules { get; } = new("Serviceless");
        public IReadOnlyList<ICodeFile> BuildFiles() => new[] { _file };
    }

    private sealed class FakeServiceVariableSource : IServiceVariableSource
    {
        private readonly string? _reportName;

        public FakeServiceVariableSource(string? reportName) { _reportName = reportName; }

        public bool Matches(Type type) => false;
        public JasperFx.CodeGeneration.Model.Variable Create(Type type) => throw new NotSupportedException();
        public bool TryFindKeyedService(Type type, string key, out JasperFx.CodeGeneration.Model.Variable? variable)
        { variable = null; return false; }
        public void ReplaceVariables(IMethodVariables method) { }
        public void StartNewType() { }
        public void StartNewMethod() { }

        // #2991: record the per-file override lifecycle so the test can assert isolation.
        public List<string> Calls { get; } = new();

        public void ReplaceServiceProvider(JasperFx.CodeGeneration.Model.Variable serviceProvider)
            => Calls.Add($"replace:{serviceProvider.Usage}");

        public void ResetServiceProvider() => Calls.Add("reset");

        public ServiceLocationReport[] ServiceLocations()
        {
            if (_reportName == null) return Array.Empty<ServiceLocationReport>();
            return new[]
            {
                new ServiceLocationReport(
                    new ServiceDescriptor(typeof(object), typeof(object), ServiceLifetime.Scoped),
                    _reportName)
            };
        }
    }
}
