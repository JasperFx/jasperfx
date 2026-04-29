using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;

namespace CodegenTests;

public class TypeLoaderTests
{
    [Fact]
    public void default_loader_for_dynamic_mode_is_DynamicTypeLoader()
    {
        var rules = new GenerationRules { TypeLoadMode = TypeLoadMode.Dynamic };
        rules.Loader.ShouldBeOfType<DynamicTypeLoader>();
    }

    [Fact]
    public void default_loader_for_static_mode_is_StaticTypeLoader()
    {
        var rules = new GenerationRules { TypeLoadMode = TypeLoadMode.Static };
        rules.Loader.ShouldBeOfType<StaticTypeLoader>();
    }

    [Fact]
    public void default_loader_for_auto_mode_is_AutoTypeLoader()
    {
        var rules = new GenerationRules { TypeLoadMode = TypeLoadMode.Auto };
        rules.Loader.ShouldBeOfType<AutoTypeLoader>();
    }

    [Fact]
    public void changing_TypeLoadMode_updates_the_default_loader()
    {
        var rules = new GenerationRules();
        rules.Loader.ShouldBeOfType<DynamicTypeLoader>();

        rules.TypeLoadMode = TypeLoadMode.Static;
        rules.Loader.ShouldBeOfType<StaticTypeLoader>();

        rules.TypeLoadMode = TypeLoadMode.Auto;
        rules.Loader.ShouldBeOfType<AutoTypeLoader>();
    }

    [Fact]
    public void explicitly_set_loader_wins_over_TypeLoadMode_changes()
    {
        var rules = new GenerationRules();
        var custom = new RecordingTypeLoader();
        rules.Loader = custom;

        // TypeLoadMode changes do not stomp on an explicit loader assignment.
        rules.TypeLoadMode = TypeLoadMode.Static;
        rules.Loader.ShouldBeSameAs(custom);

        rules.TypeLoadMode = TypeLoadMode.Auto;
        rules.Loader.ShouldBeSameAs(custom);
    }

    [Fact]
    public void assigning_null_to_loader_throws()
    {
        var rules = new GenerationRules();
        Should.Throw<ArgumentNullException>(() => rules.Loader = null!);
    }

    [Fact]
    public void custom_loader_is_invoked_through_the_extension_method()
    {
        var rules = new GenerationRules();
        var custom = new RecordingTypeLoader();
        rules.Loader = custom;

        var collection = new StubCodeFileCollection(rules);
        var file = new StubCodeFile();

        file.InitializeSynchronously(rules, collection, services: null);

        custom.SyncCalls.ShouldBe(1);
        custom.AsyncCalls.ShouldBe(0);
        custom.LastFile.ShouldBeSameAs(file);
    }

    [Fact]
    public async Task custom_loader_is_invoked_through_the_async_extension_method()
    {
        var rules = new GenerationRules();
        var custom = new RecordingTypeLoader();
        rules.Loader = custom;

        var collection = new StubCodeFileCollection(rules);
        var file = new StubCodeFile();

        await file.Initialize(rules, collection, services: null);

        custom.SyncCalls.ShouldBe(0);
        custom.AsyncCalls.ShouldBe(1);
        custom.LastFile.ShouldBeSameAs(file);
    }

    private sealed class RecordingTypeLoader : ITypeLoader
    {
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public ICodeFile? LastFile { get; private set; }

        public void Initialize(
            ICodeFile file,
            GenerationRules rules,
            ICodeFileCollection parent,
            IServiceProvider? services)
        {
            SyncCalls++;
            LastFile = file;
        }

        public Task InitializeAsync(
            ICodeFile file,
            GenerationRules rules,
            ICodeFileCollection parent,
            IServiceProvider? services)
        {
            AsyncCalls++;
            LastFile = file;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCodeFileCollection : ICodeFileCollection
    {
        public StubCodeFileCollection(GenerationRules rules)
        {
            Rules = rules;
        }

        public IReadOnlyList<ICodeFile> BuildFiles() => Array.Empty<ICodeFile>();

        public string ChildNamespace => "Test";

        public GenerationRules Rules { get; }
    }

    private sealed class StubCodeFile : ICodeFile
    {
        public string FileName => "StubFile";

        public void AssembleTypes(GeneratedAssembly assembly) { }

        public Task<bool> AttachTypes(
            GenerationRules rules,
            System.Reflection.Assembly assembly,
            IServiceProvider? services,
            string containingNamespace) => Task.FromResult(false);

        public bool AttachTypesSynchronously(
            GenerationRules rules,
            System.Reflection.Assembly assembly,
            IServiceProvider? services,
            string containingNamespace) => false;
    }
}
