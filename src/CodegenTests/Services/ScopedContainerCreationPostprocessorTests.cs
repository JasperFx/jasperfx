using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace CodegenTests.Services;

public class ScopedContainerCreationPostprocessorTests
{
    private static string EmitDirect(AsyncMode mode, Action<ScopedContainerCreation> configure, Frame? next = null)
    {
        var method = new GeneratedMethod("Foo", typeof(string)) { AsyncMode = mode };
        var frame = new ScopedContainerCreation();
        configure(frame);
        if (next != null)
        {
            frame.Next = next;
        }

        var writer = new SourceWriter();
        frame.GenerateCode(method, writer);
        return writer.Code();
    }

    [Theory]
    [InlineData(AsyncMode.AsyncTask)]
    [InlineData(AsyncMode.None)]
    public void emits_postprocessors_after_scope_line_before_next_in_registration_order(AsyncMode mode)
    {
        var code = EmitDirect(mode, f =>
        {
            f.AddPostProcessor(new LinePostprocessor("// FIRST"));
            f.AddPostProcessor(new LinePostprocessor("// SECOND"));
        }, new LinePostprocessor("// NEXT"));

        code.IndexOf("serviceScope =", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("// FIRST", StringComparison.Ordinal));
        code.IndexOf("// FIRST", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("// SECOND", StringComparison.Ordinal));
        code.IndexOf("// SECOND", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("// NEXT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AsyncMode.AsyncTask, "await using var serviceScope = _serviceScopeFactory.CreateAsyncScope();")]
    [InlineData(AsyncMode.None, "using var serviceScope = _serviceScopeFactory.CreateScope();")]
    public void no_postprocessors_produces_byte_identical_output(AsyncMode mode, string expected)
    {
        EmitDirect(mode, _ => { }).Trim().ShouldBe(expected);
    }

    [Fact]
    public void hands_the_scoped_provider_to_IUsesServiceProviderFrame_postprocessors()
    {
        var frame = new ScopedContainerCreation();
        var postprocessor = new ProviderConsumingPostprocessor();
        frame.AddPostProcessor(postprocessor);

        var chain = Substitute.For<IMethodVariables>();
        var yielded = frame.FindVariables(chain).ToArray();

        // The child received the parent's scoped provider...
        postprocessor.Provider.ShouldBeSameAs(frame.Scoped);
        postprocessor.Provider!.Usage.ShouldBe("serviceScope.ServiceProvider");

        // ...and never asked the arranger for an IServiceProvider (no second scope): the only
        // variable surfaced is the scope factory.
        yielded.ShouldHaveSingleItem().ShouldBeSameAs(frame.Factory);
    }

    [Fact]
    public void IUsesServiceProviderFrame_postprocessor_emits_against_the_handed_provider()
    {
        var method = new GeneratedMethod("Foo", typeof(string)) { AsyncMode = AsyncMode.AsyncTask };
        var frame = new ScopedContainerCreation();
        frame.AddPostProcessor(new ProviderConsumingPostprocessor());

        frame.FindVariables(Substitute.For<IMethodVariables>()).ToArray(); // hands the provider down

        var writer = new SourceWriter();
        frame.GenerateCode(method, writer);
        var code = writer.Code();

        code.ShouldContain("serviceScope.ServiceProvider");
        // exactly one scope is created — the postprocessor reused the handed provider
        CountOccurrences(code, "CreateAsyncScope").ShouldBe(1);
        code.ShouldNotContain("CreateScope(");
    }

    [Fact]
    public void downstream_next_consumes_a_postprocessor_created_variable_once_and_in_order()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("GeneratedResolver", typeof(IScopedResolver));
        var method = type.MethodFor(nameof(IScopedResolver.Resolve));

        var scoped = new ScopedContainerCreation();
        scoped.AddPostProcessor(new CreatingPostprocessor());
        method.Frames.Add(scoped);
        method.Frames.Add(new UsesFooFrame());

        var code = assembly.GenerateCode();

        // resolves through the arranger, emitted exactly once, in order: scope -> create -> use
        CountOccurrences(code, "new CodegenTests.Services.ScopedFoo()").ShouldBe(1);
        code.IndexOf("serviceScope =", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("new CodegenTests.Services.ScopedFoo()", StringComparison.Ordinal));
        code.IndexOf("new CodegenTests.Services.ScopedFoo()", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("foo.ToString()", StringComparison.Ordinal));
    }

    [Fact]
    public void a_postprocessor_resolves_its_own_dependencies_through_find_variables()
    {
        var frame = new ScopedContainerCreation();
        var dependency = new Variable(typeof(ScopedFoo), "foo");
        var postprocessor = new DependentPostprocessor();

        var chain = Substitute.For<IMethodVariables>();
        chain.FindVariable(typeof(ScopedFoo)).Returns(dependency);

        frame.AddPostProcessor(postprocessor);

        var yielded = frame.FindVariables(chain).ToArray();

        yielded.ShouldContain(frame.Factory);
        yielded.ShouldContain(dependency);
        postprocessor.Resolved.ShouldBeSameAs(dependency);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        return haystack.Split(needle).Length - 1;
    }
}

public interface IScopedResolver
{
    string Resolve();
}

public class ScopedFoo
{
}

/// <summary>A trivial postprocessor that writes a marker line.</summary>
public class LinePostprocessor : SyncFrame
{
    private readonly string _line;

    public LinePostprocessor(string line)
    {
        _line = line;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(_line);
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>A postprocessor that needs the scoped IServiceProvider and emits against it.</summary>
public class ProviderConsumingPostprocessor : SyncFrame, IUsesServiceProviderFrame
{
    public Variable? Provider { get; private set; }

    public void UseServiceProvider(Variable serviceProvider)
    {
        Provider = serviceProvider;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Uses the handed-in provider, so it asks the arranger for nothing.
        yield break;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var scopedThing = {Provider!.Usage};");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>A postprocessor that creates a new variable for downstream frames to consume.</summary>
public class CreatingPostprocessor : SyncFrame
{
    public CreatingPostprocessor()
    {
        Created = new Variable(typeof(ScopedFoo), "foo", this);
    }

    public Variable Created { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {Created.Usage} = new CodegenTests.Services.ScopedFoo();");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>A downstream frame that consumes a ScopedFoo created elsewhere.</summary>
public class UsesFooFrame : SyncFrame
{
    private Variable _foo = null!;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"return {_foo.Usage}.ToString();");
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _foo = chain.FindVariable(typeof(ScopedFoo));
        yield return _foo;
    }
}

/// <summary>A postprocessor whose own dependency is resolved through the normal arranger path.</summary>
public class DependentPostprocessor : SyncFrame
{
    public Variable? Resolved { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Resolved = chain.FindVariable(typeof(ScopedFoo));
        yield return Resolved;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var dependent = {Resolved!.Usage};");
        Next?.GenerateCode(method, writer);
    }
}
