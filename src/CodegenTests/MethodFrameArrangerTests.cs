using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;

public class MethodFrameArrangerTests
{
    [Fact]
    public void reuses_source_created_variable_from_a_different_requested_type()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("GeneratedHandler", typeof(IHttpHandlerShape));
        var method = type.MethodFor(nameof(IHttpHandlerShape.Handle));
        method.Sources.Add(new MessageContextVariableSource());

        method.Frames.Add(new DetectTenantFrame());
        method.Frames.Add(new UsesMessageBusFrame());
        method.Frames.Add(new OpenOutboxedSessionFrame());

        var code = assembly.GenerateCode();

        CountOccurrences(code, "var messageContext = new CodegenTests.TestMessageContext()").ShouldBe(1);
        code.ShouldContain("OpenSession(messageContext, tenantId)");
    }

    // wolverine#4198. VariableSource.All / NotServices are FACTORIES -- they build what they cannot
    // find. A caller that only wants to know whether the method already has one of these needs an
    // answer that is not manufactured on the spot.
    [Fact]
    public void existing_does_not_manufacture_a_variable_from_a_source()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("GeneratedHandler", typeof(IHttpHandlerShape));
        var method = type.MethodFor(nameof(IHttpHandlerShape.Handle));
        method.Sources.Add(new MessageContextVariableSource());

        var probe = new ProbeForExistingFrame(typeof(TestMessageContext));
        method.Frames.Add(probe);

        var code = assembly.GenerateCode();

        // Nothing in this method wanted a message context, so asking for one does not conjure it up
        probe.Found.ShouldBeNull();
        code.ShouldNotContain("new CodegenTests.TestMessageContext()");
    }

    [Fact]
    public void existing_finds_a_variable_the_method_already_has()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("GeneratedHandler", typeof(IHttpHandlerShape));
        var method = type.MethodFor(nameof(IHttpHandlerShape.Handle));
        method.Sources.Add(new MessageContextVariableSource());

        // This frame genuinely wants the context, so by the time the probe asks, the method has one
        method.Frames.Add(new UsesMessageBusFrame());

        var probe = new ProbeForExistingFrame(typeof(TestMessageContext));
        method.Frames.Add(probe);

        var code = assembly.GenerateCode();

        probe.Found.ShouldNotBeNull();
        probe.Found.Usage.ShouldBe("messageContext");
        CountOccurrences(code, "var messageContext = new CodegenTests.TestMessageContext()").ShouldBe(1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        return haystack.Split(needle).Length - 1;
    }
}

public interface IHttpHandlerShape
{
    void Handle();
}

public interface ITestMessageBus
{
}

public interface ITestMessageContext
{
}

public class TestMessageContext : ITestMessageBus, ITestMessageContext
{
}

public class TestOutboxedSessionFactory
{
    public void OpenSession(TestMessageContext context, string tenantId)
    {
    }
}

public class MessageContextVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(ITestMessageBus) || type == typeof(ITestMessageContext) ||
               type == typeof(TestMessageContext);
    }

    public Variable Create(Type type)
    {
        return new TestMessageContextFrame().Variable;
    }
}

public class TestMessageContextFrame : SyncFrame
{
    private Variable? _tenantId;

    public TestMessageContextFrame()
    {
        Variable = new Variable(typeof(TestMessageContext), "messageContext", this);
        creates.Add(new CastVariable(Variable, typeof(ITestMessageBus)));
        creates.Add(new CastVariable(Variable, typeof(ITestMessageContext)));
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (chain.TryFindVariableByName(typeof(string), "tenantId", out _tenantId))
        {
            yield return _tenantId;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {Variable.Usage} = new {typeof(TestMessageContext).FullName}();");
        if (_tenantId != null)
        {
            writer.WriteLine($"{Variable.Usage}.ToString();");
        }

        Next?.GenerateCode(method, writer);
    }
}

public class DetectTenantFrame : SyncFrame
{
    public DetectTenantFrame()
    {
        TenantId = new Variable(typeof(string), "tenantId", this);
    }

    public Variable TenantId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {TenantId.Usage} = \"tenant1\";");
        Next?.GenerateCode(method, writer);
    }
}

public class UsesMessageBusFrame : SyncFrame
{
    private Variable _bus = null!;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _bus = chain.FindVariable(typeof(ITestMessageBus));
        yield return _bus;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{_bus.Usage}.ToString();");
        Next?.GenerateCode(method, writer);
    }
}

public class OpenOutboxedSessionFrame : SyncFrame
{
    private Variable _context = null!;
    private Variable _tenantId = null!;
    private readonly Variable _factory = new InjectedField(typeof(TestOutboxedSessionFactory), "factory");

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _tenantId = chain.FindVariableByName(typeof(string), "tenantId");
        yield return _tenantId;

        _context = chain.FindVariable(typeof(TestMessageContext));
        yield return _context;

        yield return _factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{_factory.Usage}.OpenSession({_context.Usage}, {_tenantId.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     Asks the arranger whether the method ALREADY has a variable of the given type, and records the
///     answer. See wolverine#4198.
/// </summary>
public class ProbeForExistingFrame : SyncFrame
{
    private readonly Type _type;

    public ProbeForExistingFrame(Type type)
    {
        _type = type;
    }

    public Variable? Found { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Found = chain.TryFindVariable(_type, VariableSource.Existing);
        if (Found != null)
        {
            yield return Found;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"// probe found: {Found?.Usage ?? "nothing"}");
        Next?.GenerateCode(method, writer);
    }
}
