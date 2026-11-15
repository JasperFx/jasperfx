using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1188: an aggregate whose only parameterless constructor is non-public was built through
/// <c>RuntimeHelpers.GetUninitializedObject</c>, so its constructor body and every initializer were
/// skipped and the first Apply could dereference null. Marten 8 called the constructor reflectively.
/// The evolver now calls it through a file-scoped <c>[UnsafeAccessor]</c>, which stays trim and AOT
/// safe; GetUninitializedObject is left for aggregates with no parameterless constructor at all and for
/// the shapes a file-scoped type cannot name. The runtime check that the body and initializers run is in
/// EventTests, Bug_1188_non_public_aggregate_constructor.
/// </summary>
public class NonPublicConstructorAccessorTests
{
    private const string Accessor =
        "[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor)]";

    private const string SelfAggregatingTemplate = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;

namespace Test;

public sealed record NameAdded(string Name);

public class A
{
    __ACCESSIBILITY__ A() { }

    public Guid Id { get; set; }
    private readonly List<string> _names = new();
    public void Apply(NameAdded e) => _names.Add(e.Name);
}
";

    [Theory]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("internal")]
    [InlineData("private protected")]
    [InlineData("protected internal")]
    public void a_non_public_parameterless_constructor_is_called_through_an_unsafe_accessor(string accessibility)
    {
        var source = SelfAggregatingTemplate.Replace("__ACCESSIBILITY__", accessibility);

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        diagnostics.ShouldBeEmpty();

        var generated = generatedSources.Single(s => s.Contains("AEvolver"));
        generated.ShouldContain("snapshot ??= __JasperFxAggregateActivator.Create();");
        generated.ShouldContain("file static class __JasperFxAggregateActivator");
        generated.ShouldContain(Accessor);
        generated.ShouldContain("internal static extern global::Test.A Create();");
        generated.ShouldNotContain("GetUninitializedObject");

        compilationErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_public_parameterless_constructor_is_still_called_directly()
    {
        var source = SelfAggregatingTemplate.Replace("__ACCESSIBILITY__", "public");

        var generated = GeneratorHarness.Run(source).generatedSources.Single(s => s.Contains("AEvolver"));

        generated.ShouldContain("new global::Test.A()");
        generated.ShouldNotContain("__JasperFxAggregateActivator");
    }

    [Fact]
    public void an_aggregate_with_no_parameterless_constructor_still_uses_an_uninitialized_object()
    {
        const string source = @"
using System;
using JasperFx.Events;

namespace Test;

public sealed record NameAdded(string Name);

public record A(Guid Id)
{
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}
";

        var generated = GeneratorHarness.Run(source).generatedSources.Single(s => s.Contains("AEvolver"));

        generated.ShouldContain(
            "(global::Test.A)global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::Test.A))");
        generated.ShouldNotContain("__JasperFxAggregateActivator");

        compilationErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_projection_over_an_aggregate_with_a_private_constructor_gets_the_accessor_in_its_own_file()
    {
        const string source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Test;

public class StubOperations : IStorageOperations
{
    public bool EnableSideEffectsOnInlineProjections => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(
        string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, StubOperations, StubOperations>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() { }
}

public sealed record NameAdded(string Name);

public class Doc
{
    private Doc() { }
    public Guid Id { get; set; }
    public List<string> Names { get; } = new();
}

public partial class FirstProjection : SingleStreamProjection<Doc, Guid>
{
    public void Apply(NameAdded e, Doc doc) => doc.Names.Add(e.Name);
}

public partial class SecondProjection : SingleStreamProjection<Doc, Guid>
{
    public void Apply(NameAdded e, Doc doc) => doc.Names.Add(e.Name);
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        diagnostics.ShouldBeEmpty();

        // One file-scoped accessor per generated file, so two projections over one aggregate never collide
        foreach (var name in new[] { "FirstProjection", "SecondProjection" })
        {
            var generated = generatedSources.Single(s => s.Contains($"typeof(global::Test.{name})"));
            generated.ShouldContain("__JasperFxAggregateActivator.Create()");
            generated.ShouldContain("internal static extern global::Test.Doc Create();");
            generated.ShouldNotContain("GetUninitializedObject");
        }

        compilationErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_member_injected_projection_gets_the_accessor_at_namespace_level()
    {
        // A private nested projection takes the member-injection path (#462), so the generated file re-opens
        // Container. The accessor must land at namespace level, not inside
        // it, because file-scoped types cannot be nested
        const string source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Test;

public class StubOperations : IStorageOperations
{
    public bool EnableSideEffectsOnInlineProjections => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(
        string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, StubOperations, StubOperations>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() { }
}

public sealed record NameAdded(string Name);

public class Doc
{
    private Doc() { }
    public Guid Id { get; set; }
    public List<string> Names { get; } = new();
}

public partial class Container
{
    private partial class NestedProjection : SingleStreamProjection<Doc, Guid>
    {
        public void Apply(NameAdded e, Doc doc) => doc.Names.Add(e.Name);
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        diagnostics.ShouldBeEmpty();
        var generated = generatedSources.Single(s => s.Contains("__JasperFxAggregateActivator.Create()"));
        generated.ShouldContain("partial class Container");
        generated.ShouldNotContain("file sealed class");

        compilationErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_generic_aggregate_keeps_the_uninitialized_object_path()
    {
        const string source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Test;

public class StubOperations : IStorageOperations
{
    public bool EnableSideEffectsOnInlineProjections => false;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(
        string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, StubOperations, StubOperations>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() { }
}

public sealed record NameAdded(string Name);

public class Doc<T>
{
    private Doc() { }
    public Guid Id { get; set; }
    public List<string> Names { get; } = new();
}

public partial class GenericDocProjection : SingleStreamProjection<Doc<int>, Guid>
{
    public void Apply(NameAdded e, Doc<int> doc) => doc.Names.Add(e.Name);
}
";

        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = string.Join("\n", generatedSources);

        generated.ShouldNotContain("__JasperFxAggregateActivator");
        generated.ShouldContain("GetUninitializedObject(typeof(global::Test.Doc<int>))");

        compilationErrors(source).ShouldBeEmpty();
    }

    private static string[] compilationErrors(string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "NonPublicConstructorAccessorTests",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        return output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
    }

    private static List<MetadataReference> References()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        ];
    }
}
