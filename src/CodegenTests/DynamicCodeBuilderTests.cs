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
        private readonly ICodeFile _file;
        public RecordingCollection(ICodeFile file) { _file = file; }
        public string ChildNamespace => "RecordedNamespace";
        public GenerationRules Rules { get; } = new("RecordedNamespace");
        public IReadOnlyList<ICodeFile> BuildFiles() => new[] { _file };
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
