using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using NSubstitute;
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

    // wolverine#4232. Native AOT cannot emit a type into its image, so neither the dynamic loader nor the
    // auto loader's fallback to it can work there. Auto exists to adapt to the platform, so it adapts;
    // Dynamic asked for something the platform cannot do, and saying so beats the obscure failure it used
    // to reach later. This is also what keeps the trimmer's IL2026/IL3050 out of a consumer's publish --
    // ILC substitutes RuntimeFeature.IsDynamicCodeSupported with false and removes the branch that
    // constructs them.
    [Fact]
    public void without_dynamic_code_auto_mode_falls_back_to_the_static_loader()
    {
        GenerationRules.DefaultLoaderFor(TypeLoadMode.Auto, dynamicCodeSupported: false)
            .ShouldBeOfType<StaticTypeLoader>();
    }

    [Fact]
    public void without_dynamic_code_static_mode_is_unchanged()
    {
        GenerationRules.DefaultLoaderFor(TypeLoadMode.Static, dynamicCodeSupported: false)
            .ShouldBeOfType<StaticTypeLoader>();
    }

    [Fact]
    public void without_dynamic_code_dynamic_mode_says_so_instead_of_failing_later()
    {
        var ex = Should.Throw<PlatformNotSupportedException>(() =>
            GenerationRules.DefaultLoaderFor(TypeLoadMode.Dynamic, dynamicCodeSupported: false));

        // The message has to name the way out, not just the wall.
        ex.Message.ShouldContain(nameof(TypeLoadMode.Static));
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
    public void static_loader_throws_when_type_missing_and_not_in_codegen_command()
    {
        // Production runtime path: `WithinCodegenCommand=false`, pre-built
        // type missing → throw with a clear "missing pre-generated type"
        // message so AOT-mode hosts fail fast instead of silently going
        // through Roslyn. Regression coverage for the AOT contract.
        var loader = new StaticTypeLoader();
        var rules = new GenerationRules { TypeLoadMode = TypeLoadMode.Static };
        var collection = new StubCodeFileCollection(rules);
        var file = new StubCodeFile();

        DynamicCodeBuilder.WithinCodegenCommand.ShouldBeFalse();
        Should.Throw<ExpectedTypeMissingException>(() =>
            loader.Initialize(file, rules, collection, services: null));
    }

    [Fact]
    public void static_loader_falls_back_to_runtime_compile_when_in_codegen_command()
    {
        // Codegen-tool-time path (marten#4354): when invoked from inside
        // `codegen write`/`preview` AND no pre-built type is available,
        // route through DynamicTypeLoader.CompileAndAttach so callers
        // (like ProviderGraph.CreateDocumentProvider<T> building a
        // compiled-query plan) get an attached type, not a null. Caller
        // signal — without an IAssemblyGenerator registered, the
        // DynamicTypeLoader fallback throws InvalidOperationException
        // pointing at the missing registration. That throw is what
        // proves we *entered* the fallback (vs the pre-fix behavior of
        // silently returning).
        var loader = new StaticTypeLoader();
        var rules = new GenerationRules { TypeLoadMode = TypeLoadMode.Static };
        var collection = new StubCodeFileCollection(rules);
        var file = new StubCodeFile();

        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() =>
                loader.Initialize(file, rules, collection, services: null));
            ex.Message.ShouldContain("IAssemblyGenerator");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
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

    // jasperfx#1041. GeneratedNamespace is what separates these: this test assembly exports public types under
    // "CodegenTests" and none at all under "Nothing.Generated.Here".
    private static GenerationRules staticRulesFor(string generatedNamespace) => new(generatedNamespace)
    {
        TypeLoadMode = TypeLoadMode.Static,
        ApplicationAssembly = typeof(TypeLoaderTests).Assembly
    };

    [Fact]
    public void static_loader_says_how_to_fix_it_when_nothing_generated_is_in_the_assembly()
    {
        var rules = staticRulesFor("Nothing.Generated.Here");
        var collection = new StubCodeFileCollection(rules);

        var ex = Should.Throw<ExpectedTypeMissingException>(() =>
            new StaticTypeLoader().Initialize(new StubCodeFile(), rules, collection, services: null));

        ex.Message.ShouldContain("StubFile");
        ex.Message.ShouldContain("found no pre-generated types at all");
        ex.Message.ShouldContain("Nothing.Generated.Here");
        ex.Message.ShouldContain("codegen write");
        ex.Message.ShouldContain(rules.GeneratedCodeOutputPath);
        ex.Message.ShouldContain(nameof(GenerationRules.ApplicationAssembly));
    }

    [Fact]
    public void static_loader_says_the_code_is_stale_when_other_generated_types_are_there()
    {
        var rules = staticRulesFor("CodegenTests");
        var collection = new StubCodeFileCollection(rules);

        var ex = Should.Throw<ExpectedTypeMissingException>(() =>
            new StaticTypeLoader().Initialize(new StubCodeFile(), rules, collection, services: null));

        ex.Message.ShouldContain("out of date");
        ex.Message.ShouldContain("codegen write");
        ex.Message.ShouldNotContain("found no pre-generated types at all");
    }

    [Fact]
    public void under_native_aot_an_empty_scan_names_the_rooting_companion()
    {
        var rules = staticRulesFor("Nothing.Generated.Here");

        var message = rules.ApplicationAssembly.DescribeMissingPreGeneratedTypes(rules, dynamicCodeSupported: false);

        message.ShouldContain("Native AOT");
        message.ShouldContain("ILC trimmed it");
        message.ShouldContain(nameof(AotRootsCompanionExtensions.AddAotRoots));
        message.ShouldContain("codegen write");
    }

    [Fact]
    public void under_native_aot_a_partial_scan_also_names_the_rooting_companion()
    {
        var rules = staticRulesFor("CodegenTests");

        var message = rules.ApplicationAssembly.DescribeMissingPreGeneratedTypes(rules, dynamicCodeSupported: false);

        message.ShouldContain("out of date");
        message.ShouldContain(nameof(AotRootsCompanionExtensions.AddAotRoots));
    }

    [Fact]
    public void asserting_pre_built_types_says_how_to_fix_it()
    {
        var collection = new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"), new StubCodeFile());

        var ex = Should.Throw<MissingTypeException>(() =>
            collection.AssertPreBuildTypesExist(Substitute.For<IServiceProvider>()));

        ex.Message.ShouldContain("found no pre-generated types at all");
    }

    [Fact]
    public void asserting_pre_built_types_is_quiet_when_no_code_file_expects_a_type()
    {
        new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"))
            .AssertPreBuildTypesExist(Substitute.For<IServiceProvider>());
    }

    [Fact]
    public async Task loading_prebuilt_types_fails_loudly_when_a_static_scan_comes_up_empty()
    {
        var collection = new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"), new StubCodeFile());
        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(), [collection]);

        var ex = await Should.ThrowAsync<ExpectedTypeMissingException>(() => builder.LoadPrebuiltTypes());

        ex.Message.ShouldContain("StubFile");
        ex.Message.ShouldContain("found no pre-generated types at all");
        ex.Message.ShouldContain("codegen write");
    }

    [Fact]
    public async Task loading_prebuilt_types_names_every_missing_file_and_uses_the_supplied_assembly()
    {
        var rules = staticRulesFor("Nothing.Generated.Here");
        var collection = new StubCodeFileCollection(rules, new StubCodeFile(), new StubCodeFile("OtherFile"));
        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(), [collection]);

        var ex = await Should.ThrowAsync<ExpectedTypeMissingException>(() =>
            builder.LoadPrebuiltTypes(typeof(GenerationRules).Assembly));

        ex.Message.ShouldContain("2 expected pre-built type(s)");
        ex.Message.ShouldContain("StubFile, OtherFile");
        ex.Message.ShouldContain(typeof(GenerationRules).Assembly.FullName!);
    }

    [Fact]
    public async Task loading_prebuilt_types_is_quiet_for_an_application_with_no_generated_code()
    {
        // The other half of the same assembly state as the test above: nothing generated is in here, and in
        // Static mode. What differs is that no code file expected anything.
        var collection = new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"));
        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(), [collection]);

        await builder.LoadPrebuiltTypes();
    }

    [Fact]
    public async Task loading_prebuilt_types_is_quiet_when_every_expected_type_is_found()
    {
        var collection = new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"),
            new StubCodeFile(found: true));
        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(), [collection]);

        await builder.LoadPrebuiltTypes();
    }

    [Theory]
    [InlineData(TypeLoadMode.Dynamic)]
    [InlineData(TypeLoadMode.Auto)]
    public async Task loading_prebuilt_types_leaves_a_miss_to_a_loader_that_can_compile_it(TypeLoadMode mode)
    {
        var rules = staticRulesFor("Nothing.Generated.Here");
        rules.TypeLoadMode = mode;

        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(),
            [new StubCodeFileCollection(rules, new StubCodeFile())]);

        await builder.LoadPrebuiltTypes();
    }

    [Fact]
    public async Task loading_prebuilt_types_leaves_a_miss_to_a_custom_loader()
    {
        var rules = staticRulesFor("Nothing.Generated.Here");
        rules.Loader = new RecordingTypeLoader();

        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(),
            [new StubCodeFileCollection(rules, new StubCodeFile())]);

        await builder.LoadPrebuiltTypes();
    }

    [Fact]
    public async Task loading_prebuilt_types_expects_misses_within_a_codegen_command()
    {
        var builder = new DynamicCodeBuilder(Substitute.For<IServiceProvider>(),
            [new StubCodeFileCollection(staticRulesFor("Nothing.Generated.Here"), new StubCodeFile())]);

        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            await builder.LoadPrebuiltTypes();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
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
        private readonly ICodeFile[] _files;

        public StubCodeFileCollection(GenerationRules rules, params ICodeFile[] files)
        {
            Rules = rules;
            _files = files;
        }

        public IReadOnlyList<ICodeFile> BuildFiles() => _files;

        public string ChildNamespace => "Test";

        public GenerationRules Rules { get; }
    }

    private sealed class StubCodeFile(string fileName = "StubFile", bool found = false) : ICodeFile
    {
        public string FileName => fileName;

        public void AssembleTypes(GeneratedAssembly assembly) { }

        public Task<bool> AttachTypes(
            GenerationRules rules,
            System.Reflection.Assembly assembly,
            IServiceProvider? services,
            string containingNamespace) => Task.FromResult(found);

        public bool AttachTypesSynchronously(
            GenerationRules rules,
            System.Reflection.Assembly assembly,
            IServiceProvider? services,
            string containingNamespace) => found;
    }
}
